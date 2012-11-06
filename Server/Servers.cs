// 
//  ServerHandler.cs
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
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;

using log4net;

using XG.Core;
using XG.Server.Helper;
using XG.Server.Irc;

namespace XG.Server
{
	public delegate void DownloadDelegate(Packet aPack, Int64 aChunk, IPAddress aIp, int aPort);

	/// <summary>
	/// This class describes a irc server connection handler
	/// it does the following things
	/// - connect to or disconnect from an irc server
	/// - handling of global bot downloads
	/// - splitting and merging the files to download
	/// - writing files to disk
	/// - timering some clean up tasks
	/// </summary>
	public class Servers
	{
		#region VARIABLES

		static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		Parser _ircParser;
		public Parser IrcParser
		{
			set
			{
				if(_ircParser != null)
				{
					_ircParser.Parent = null;
					_ircParser.AddDownload -= new DownloadDelegate (BotConnect);
					_ircParser.RemoveDownload -= new BotDelegate (BotDisconnect);
				}
				_ircParser = value;
				if(_ircParser != null)
				{
					_ircParser.Parent = this;
					_ircParser.AddDownload += new DownloadDelegate (BotConnect);
					_ircParser.RemoveDownload += new BotDelegate (BotDisconnect);
				}
			}
		}

		public FileActions FileActions { get; set; }

		Dictionary<Core.Server, ServerConnection> _servers;
		Dictionary<Packet, BotConnection> _downloads;

		#endregion

		#region INIT

		public Servers()
		{
			_servers = new Dictionary<Core.Server, ServerConnection>();
			_downloads = new Dictionary<Packet, BotConnection>();

			// create my stuff if its not there
			new System.IO.DirectoryInfo(Settings.Instance.ReadyPath).Create();
			new System.IO.DirectoryInfo(Settings.Instance.TempPath).Create();

			// start the timed tasks
			new Thread(new ThreadStart(RunTimer)).Start();
		}

		#endregion

		#region SERVER

		/// <summary>
		/// Connects to the given server by using a new ServerConnnect class
		/// </summary>
		/// <param name="aServer"></param>
		public void ServerConnect (Core.Server aServer)
		{
			if (!_servers.ContainsKey (aServer))
			{
				ServerConnection con = new ServerConnection ();
				con.FileActions = FileActions;
				con.Server = aServer;
				con.IrcParser = _ircParser;

				con.Connection = new Server.Connection.Connection();
				con.Connection.Hostname = aServer.Name;
				con.Connection.Port = aServer.Port;
				con.Connection.MaxData = 0;

				_servers.Add (aServer, con);

				con.Connected += new ServerDelegate(ServerConnected);
				con.Disconnected += new ServerSocketErrorDelegate(ServerDisconnected);

				// start a new thread wich connects to the given server
				new Thread(delegate() { con.Connection.Connect(); }).Start();
			}
			else
			{
				_log.Error("ConnectServer(" + aServer.Name + ") server is already in the dictionary");
			}
		}
		void ServerConnected(Core.Server aServer)
		{
			// nom nom nom ...
		}

		/// <summary>
		/// Disconnects the given server
		/// </summary>
		/// <param name="aServer"></param>
		public void ServerDisconnect(Core.Server aServer)
		{
			if (_servers.ContainsKey(aServer))
			{
				ServerConnection con = _servers[aServer];

				if (con.Connection != null)
				{
					con.Connection.Disconnect();
				}
			}
			else
			{
				_log.Error("DisconnectServer(" + aServer.Name + ") server is not in the dictionary");
			}
		}
		void ServerDisconnected(Core.Server aServer, SocketErrorCode aValue)
		{
			if (_servers.ContainsKey (aServer))
			{
				ServerConnection con = _servers[aServer];

				if (aServer.Enabled)
				{
					// disable the server if the host was not found
					// this is also triggered if we have no internet connection and disables all channels
					/*if(	aValue == SocketErrorCode.HostNotFound ||
						aValue == SocketErrorCode.HostNotFoundTryAgain)
					{
						aServer.Enabled = false;
					}
					else*/
					{
						int time = Settings.Instance.ReconnectWaitTime;
						switch(aValue)
						{
							case SocketErrorCode.HostIsDown:
							case SocketErrorCode.HostUnreachable:
							case SocketErrorCode.ConnectionTimedOut:
							case SocketErrorCode.ConnectionRefused:
								time = Settings.Instance.ReconnectWaitTimeLong;
								break;
//							case SocketErrorCode.HostNotFound:
//							case SocketErrorCode.HostNotFoundTryAgain:
//								time = Settings.Instance.ReconnectWaitTimeReallyLong;
//								break;
						}
						new Timer(new TimerCallback(ServerReconnect), aServer, time * 1000, System.Threading.Timeout.Infinite);
					}
				}
				else
				{
					con.Connected -= new ServerDelegate(ServerConnected);
					con.Disconnected -= new ServerSocketErrorDelegate(ServerDisconnected);

					con.Server = null;
					con.IrcParser = null;

					_servers.Remove(aServer);
				}

				con.Connection = null;
			}
			else
			{
				_log.Error("ServerConnectionDisconnected(" + aServer.Name + ", " + aValue + ") server is not in the dictionary");
			}
		}

		void ServerReconnect(object aServer)
		{
			Core.Server tServer = aServer as Core.Server;

			if (_servers.ContainsKey(tServer))
			{
				ServerConnection con = _servers[tServer];

				if (tServer.Enabled)
				{
					_log.Error("ReconnectServer(" + tServer.Name + ")");

					// TODO do we need a new connection here?
					con.Connection = new Server.Connection.Connection();
					con.Connection.Hostname = tServer.Name;
					con.Connection.Port = tServer.Port;
					con.Connection.MaxData = 0;

					con.Connection.Connect();
				}
			}
			else
			{
				_log.Error("ReconnectServer(" + tServer.Name + ") server is not in the dictionary");
			}
		}

		#endregion

		#region BOT

		/// <summary>
		/// 
		/// </summary>
		/// <param name="aPack"></param>
		/// <param name="aChunk"></param>
		/// <param name="aIp"></param>
		/// <param name="aPort"></param>
		void BotConnect(Packet aPack, Int64 aChunk, IPAddress aIp, int aPort)
		{
			if (!_downloads.ContainsKey(aPack))
			{
				new Thread(delegate()
				{
					BotConnection con = new BotConnection();
					con.FileActions = FileActions;
					con.Packet = aPack;
					con.StartSize = aChunk;
	
					con.Connection = new Server.Connection.Connection();
					con.Connection.Hostname = aIp.ToString();
					con.Connection.Port = aPort;
					con.Connection.MaxData = aPack.RealSize - aChunk;
	
					con.Connected += new PacketBotConnectDelegate(BotConnected);
					con.Disconnected += new PacketBotConnectDelegate(BotDisconnected);
	
					_downloads.Add(aPack, con);
					con.Connection.Connect();
				}).Start();
			}
			else
			{
				// uhh - that should not happen
				_log.Error("IrcParserAddDownload(" + aPack.Name + ") is already downloading");
			}
		}
		void BotConnected (Packet aPack, BotConnection aCon)
		{
		}

		void BotDisconnect (Bot aBot)
		{
			foreach (var kvp in _downloads)
			{
				if (kvp.Key.Parent == aBot)
				{
					kvp.Value.Connection.Disconnect();
					break;
				}
			}
		}
		void BotDisconnected(Packet aPacket, BotConnection aCon)
		{
			aCon.Packet = null;
			aCon.Connection = null;

			if (_downloads.ContainsKey(aPacket))
			{
				aCon.Connected -= new PacketBotConnectDelegate(BotConnected);
				aCon.Disconnected -= new PacketBotConnectDelegate(BotDisconnected);
				_downloads.Remove(aPacket);

				try
				{
					// if the connection never connected, there will be no part!
					// and if we manually killed stopped the packet there will be no parent of the part
					if(aCon.Part != null && aCon.Part.Parent != null)
					{
						// do this here because the bothandler sets the part state and after this we can check the file
						FileActions.CheckFile(aCon.Part.Parent);
					}
				}
				catch (Exception ex)
				{
					_log.Fatal("bot_Disconnected()", ex);
				}

				try
				{
					ServerConnection sc = _servers[aPacket.Parent.Parent.Parent];
					sc.CreateTimer(aPacket.Parent, Settings.Instance.CommandWaitTime, false);
				}
				catch (Exception ex)
				{
					_log.Fatal("bot_Disconnected() request", ex);
				}
			}
		}

		#endregion

		#region TIMER TASKS

		void RunTimer()
		{
			while (true)
			{
				foreach (var kvp in _servers)
				{
					ServerConnection sc = kvp.Value;
					if (sc.IsRunning)
					{
						sc.TriggerTimerRun();
					}
				}

				Thread.Sleep(Settings.Instance.RunLoopTime * 1000);
			}
		}

		#endregion
	}
}
