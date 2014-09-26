﻿// 
//  Packets.cs
//  This file is part of XG - XDCC Grabscher
//  http://www.larsformella.de/lang/en/portfolio/programme-software/xg
//
//  Author:
//       Lars Formella <ich@larsformella.de>
// 
//  Copyright (c) 2012 Lars Formella
// 
//  This program is free software; you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation; either version 2 of the License, or
//  (at your option) any later version.
// 
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU General Public License for more details.
// 
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
//  

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using XG.Extensions;
using XG.Model.Domain;
using log4net;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace XG.Plugin.Webserver.Search
{
	public static class Packets
	{
		#region VARIABLES

		static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		static Servers _servers;

		public static Servers Servers
		{
			get { return _servers; }
			set
			{
				if (_servers != null)
				{
					_servers.OnAdded -= ObjectAdded;
					_servers.OnRemoved -= ObjectRemoved;
					_servers.OnChanged -= ObjectChanged;
				}
				_servers = value;
				if (_servers != null)
				{
					_servers.OnAdded += ObjectAdded;
					_servers.OnRemoved += ObjectRemoved;
					_servers.OnChanged += ObjectChanged;
				}
			}
		}

		static Hashtable _packets = new Hashtable();

		static IndexWriter _writer;
		static Directory _dir;
		static Analyzer _analyzer;

		const int MAX_WILDCARDS_REPLACEMENTS = 50;

		static bool _saveNeeded;

		#endregion

		#region REPOSITORY EVENTS

		static void ObjectAdded(object aSender, EventArgs<AObject, AObject> aEventArgs)
		{
			var packet = aEventArgs.Value2 as Packet;
			if (packet != null)
			{
				AddToIndex(packet);
			}
		}

		static void ObjectRemoved(object aSender, EventArgs<AObject, AObject> aEventArgs)
		{
			var packet = aEventArgs.Value2 as Packet;
			if (packet != null)
			{
				RemoveFromIndex(packet);
			}
		}

		static void ObjectChanged(object aSender, EventArgs<AObject, string[]> aEventArgs)
		{
			var bot = aEventArgs.Value1 as Bot;
			if (bot != null && aEventArgs.Value2.Contains("Connected"))
			{
				foreach (var pack in bot.Packets)
				{
					UpdateIndex(pack);
				}
			}

			var packet = aEventArgs.Value1 as Packet;
			if (packet != null)
			{
				UpdateIndex(packet);
			}

			var file = aEventArgs.Value1 as File;
			if (file != null && file.Packet != null)
			{
				UpdateIndex(file.Packet);
			}
		}

		#endregion

		public static void Initialize()
		{
			_dir = new RAMDirectory(); //FSDirectory.Open(new DirectoryInfo(@"C:/test_lucene"));
			_analyzer = new StandardAnalyzer(Lucene.Net.Util.Version.LUCENE_30, new HashSet<string>());
			_writer = new IndexWriter(_dir, _analyzer, IndexWriter.MaxFieldLength.LIMITED);

			Packet[] packets = (from server in Servers.All from channel in server.Channels from bot in channel.Bots from packet in bot.Packets select packet).ToArray();
			foreach (var packet in packets)
			{
				AddToIndex(packet);
			}

			Save();
		}

		public static Results Search(string aTerm, bool aShowOfflineBots, int aStart, int aLimit, string aSort, bool aReverse)
		{
			var results = new Results();
			var sort = BuildSort(aSort, aReverse);

			var searcher = new IndexSearcher(_writer.GetReader());
			var terms = GenerateTerms(aTerm);
			foreach (string term in terms)
			{
				var query = BuildQuery(term, aShowOfflineBots);
				var res = Search(searcher, query, sort, aStart, aStart + aLimit);
				if (res.Total > 0)
				{
					results.Packets.Add(term, res.Packets);
					results.Total += res.Total;
				}
			}

			return results;
		}

		public static Result Search(IndexSearcher aSercher, Query aQuery, Sort aSort, int aStart, int aStop)
		{
			TopDocs resultDocs = aSercher.Search(aQuery, null, aStop, aSort);
			var packets = new List<Packet>();
			for (int a = aStart; a < aStop; a++)
			{
				if (resultDocs.TotalHits <= a)
				{
					break;
				}
				var documentFromSearcher = aSercher.Doc(resultDocs.ScoreDocs[a].Doc);
				packets.Add((Packet) _packets[documentFromSearcher.Get("Guid")]);
			}
			return new Result { Total = resultDocs.TotalHits, Packets = packets };
		}

		static string[] GenerateTerms(string aTerm)
		{
			aTerm = aTerm.ToLower();
			if (!aTerm.Contains("**"))
			{
				return new[] { aTerm };
			}
			else
			{
				// atm we just support 2 simultanous wildcards
				int wildcardCount = new Regex("\\*\\*").Matches(aTerm).Count;
				if (wildcardCount > 2)
				{
					return new[] { aTerm };
				}

				var results = new Dictionary<int, string>();

				// split and queue the elemens
				var strings = new Queue<string>();
				if (aTerm.StartsWith("**", StringComparison.CurrentCulture))
				{
					strings.Enqueue("");
				}
				foreach (var str in aTerm.Split(new[] { "**" }, StringSplitOptions.RemoveEmptyEntries))
				{
					strings.Enqueue(str);
				}
				if (aTerm.EndsWith("**", StringComparison.CurrentCulture))
				{
					strings.Enqueue("");
				}

				// first segment
				var str1 = strings.Dequeue();
				for (int a = 1; a <= Math.Pow(MAX_WILDCARDS_REPLACEMENTS, wildcardCount); a++)
				{
					results.Add(a, str1);
				}

				// second segment if wanted
				if (wildcardCount == 2)
				{
					var str2 = strings.Dequeue();
					int firstCount = 1;
					int secondCount = 1;
					for (int a = 1; a <= results.Count; a++)
					{
						if (secondCount > MAX_WILDCARDS_REPLACEMENTS)
						{
							secondCount = 1;
							firstCount++;
						}
						results[a] += firstCount.ToString("D2") + str2;
						secondCount++;
					}
				}

				// last segment
				var str3 = strings.Dequeue();
				int secondCount1 = 1;
				for (int a = 1; a <= results.Count; a++)
				{
					if (secondCount1 > MAX_WILDCARDS_REPLACEMENTS)
					{
						secondCount1 = 1;
					}
					results[a] += secondCount1.ToString("D2") + str3;
					secondCount1++;
				}

				return results.Values.ToArray();
			}
		}

		static BooleanQuery BuildQuery(string aTerm, bool aShowOfflineBots)
		{
			var query = new BooleanQuery();
			foreach (string str in aTerm.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
			{
				if (str.StartsWith("-", StringComparison.CurrentCulture))
				{
					query.Add(new TermQuery(new Term("Name", str.Substring(1))), Occur.MUST_NOT);
				}
				else
				{
					query.Add(new TermQuery(new Term("Name", str)), Occur.MUST);
				}
			}
			if (!aShowOfflineBots)
			{
				query.Add(new TermQuery(new Term("Online", "1")), Occur.MUST);
			}
			return query;
		}

		static Sort BuildSort(string aSort, bool aReverse)
		{
			switch (aSort)
			{
				case "Id":
				case "Size":
				case "Speed":
				case "TimeMissing":
				case "LastMentioned":
					return new Sort(new SortField(aSort, SortField.LONG, aReverse));

				default:
					return new Sort(new SortField("Name", SortField.STRING, aReverse));
			}
		}

		#region INDEX

		static void AddToIndex(Packet aPacket)
		{
			_writer.AddDocument(PacketToDocument(aPacket));
			_packets.Add(aPacket.Guid.ToString(), aPacket);
			_saveNeeded = true;
		}

		static void UpdateIndex(Packet aPacket)
		{
			_writer.UpdateDocument(new Term("Guid", aPacket.Guid.ToString()), PacketToDocument(aPacket));
			_saveNeeded = true;
		}

		static void RemoveFromIndex(Packet aPacket)
		{
			_writer.DeleteDocuments(new TermQuery(new Term("Guid", aPacket.Guid.ToString())));
			_packets.Remove(aPacket.Guid.ToString());
			_saveNeeded = true;
		}

		public static void Save()
		{
			if (_saveNeeded)
			{
				_writer.Optimize();
				_writer.Commit();
				_saveNeeded = false;
			}
		}

		static Document PacketToDocument(Packet aPacket)
		{
			var doc = new Document();
			doc.Add(new Field("Guid", aPacket.Guid.ToString(), Field.Store.YES, Field.Index.NOT_ANALYZED));
			doc.Add(new Field("Id", "" + aPacket.Id, Field.Store.YES, Field.Index.NOT_ANALYZED));
			doc.Add(new Field("Name", aPacket.Name.Replace("_", " ").Replace("-", " ").Replace(".", " "), Field.Store.YES, Field.Index.ANALYZED));
			doc.Add(new Field("Size", "" + aPacket.Size, Field.Store.YES, Field.Index.NOT_ANALYZED ));
			doc.Add(new Field("Speed", "" + (aPacket.File != null ? aPacket.File.Speed : 0), Field.Store.YES, Field.Index.NOT_ANALYZED));
			doc.Add(new Field("TimeMissing", "" + (aPacket.File != null ? aPacket.File.TimeMissing : 0), Field.Store.YES, Field.Index.NOT_ANALYZED));
			doc.Add(new Field("LastMentioned", "" + aPacket.LastMentioned.ToTimestamp(), Field.Store.YES, Field.Index.NOT_ANALYZED));
			doc.Add(new Field("Online", aPacket.Parent.Connected ? "1" : "0", Field.Store.YES, Field.Index.NOT_ANALYZED));
			return doc;
		}

		#endregion
	}
}
