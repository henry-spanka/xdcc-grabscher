//
//  IrcServer.cs
//  This file is part of XG - XDCC Grabscher
//  http://www.larsformella.de/lang/en/portfolio/programme-software/xg
//
//  Author:
//       Lars Formella <ich@larsformella.de>
//
//  Copyright (c) 2013 Lars Formella
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
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using XG.Core;
using XG.Server.Helper;
using XG.Server.Worker;

using Meebey.SmartIrc4net;
using log4net;

namespace XG.Server.Plugin.Core.Irc
{
	public delegate void BotDelegate(Bot aBot);

	public class IrcConnection : AWorker
	{
		#region VARIABLES

		static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		public IrcClient Client { get; private set; }
		string _iam;
		
		readonly Dictionary<Bot, DateTime> _botQueue = new Dictionary<Bot, DateTime>();
		readonly Dictionary<XG.Core.Channel, DateTime> _channelQueue = new Dictionary<XG.Core.Channel, DateTime>();
		readonly Dictionary<string, DateTime> _latestPacketRequests = new Dictionary<string, DateTime>();

		XG.Core.Server _server;
		public XG.Core.Server Server
		{
			get
			{
				return _server;
			}
			set
			{
				if (_server != null)
				{
					_server.OnAdded -= ObjectAdded;
					_server.OnRemoved -= ObjectRemoved;
					_server.OnEnabledChanged -= EnabledChanged;
				}
				_server = value;
				if (_server != null)
				{
					_server.OnAdded += ObjectAdded;
					_server.OnRemoved += ObjectRemoved;
					_server.OnEnabledChanged += EnabledChanged;
				}
			}
		}

		Parser.Parser _parser;
		public Parser.Parser Parser
		{
			get
			{
				return _parser;
			}
			set
			{
				if (_parser != null)
				{
					_parser.OnJoinChannel -= JoinChannel;
					_parser.OnJoinChannelsFromBot -= JoinChannelsFromBot;
					_parser.OnQueueRequestFromBot -= QueueRequestFromBot;
					_parser.OnSendData -= SendData;
					_parser.OnSendPrivateMessage -= SendPrivateMessage;
					_parser.OnUnRequestFromBot -= UnRequestFromBot;
				}
				_parser = value;
				if (_parser != null)
				{
					_parser.OnJoinChannel += JoinChannel;
					_parser.OnJoinChannelsFromBot += JoinChannelsFromBot;
					_parser.OnQueueRequestFromBot += QueueRequestFromBot;
					_parser.OnSendData += SendData;
					_parser.OnSendPrivateMessage += SendPrivateMessage;
					_parser.OnUnRequestFromBot += UnRequestFromBot;
				}
			}
		}

		public FileActions FileActions { get; set; }

		#endregion

		#region EVENTS

		public event ServerDelegate OnDisconnected;

		#endregion

		#region EVENTHANDLER
		
		void ObjectAdded(AObject aParent, AObject aObj)
		{
			var aChan = aObj as XG.Core.Channel;
			if (aChan != null)
			{
				if (aChan.Enabled)
				{
					Client.RfcJoin(aChan.Name);
				}
			}
		}

		void ObjectRemoved(AObject aParent, AObject aObj)
		{
			var aChan = aObj as XG.Core.Channel;
			if (aChan != null)
			{
				var packets = (from bot in aChan.Bots from packet in bot.Packets select packet).ToArray();
				foreach (Packet tPack in packets)
				{
					tPack.Enabled = false;
				}

				Client.RfcPart(aChan.Name);
			}
		}

		void EnabledChanged(AObject aObj)
		{
			var aChan = aObj as XG.Core.Channel;
			if (aChan != null)
			{
				if (aChan.Enabled)
				{
					Client.RfcJoin(aChan.Name);
				}
				else
				{
					Client.RfcPart(aChan.Name);
				}
			}
			
			var tPack = aObj as Packet;
			if (tPack != null)
			{
				Bot tBot = tPack.Parent;

				if (tPack.Enabled)
				{
					if (tBot.OldestActivePacket() == tPack)
					{
						RequestFromBot(tBot);
					}
				}
				else
				{
					if (tBot.State == Bot.States.Waiting || tBot.State == Bot.States.Active)
					{
						Packet tmp = tBot.CurrentQueuedPacket;
						if (tmp == tPack)
						{
							UnRequestFromBot(tBot);
						}
					}
				}
			}
		}

		#endregion

		#region IRC EVENTHANDLER

		void JoinChannel (XG.Core.Server aServer, string aData)
		{
			if (aServer == Server)
			{
				_log.Info("JoinChannel(" + aData + ")");
				Client.RfcJoin(aData);
			}
		}

		void JoinChannelsFromBot (XG.Core.Server aServer, Bot aBot)
		{
			if (aServer == Server)
			{
				var user = Client.GetIrcUser(aBot.Name);
				if (user != null)
				{
					_log.Info("JoinChannelsFromBot(" + aBot + ")");
					Client.RfcJoin(user.JoinedChannels);
					AddBotToQueue(aBot, Settings.Instance.CommandWaitTime);
				}
			}
		}

		void QueueRequestFromBot (XG.Core.Server aServer, Bot aBot, int aInt)
		{
			if (aServer == Server)
			{
				AddBotToQueue(aBot, aInt);
			}
		}

		void UnRequestFromBot (XG.Core.Server aServer, Bot aBot)
		{
			if (aServer == Server)
			{
				UnRequestFromBot(aBot);
			}
		}

		void SendPrivateMessage (XG.Core.Server aServer, Bot aBot, string aData)
		{
			if (aServer == Server)
			{
				_log.Info("SendPrivateMessage(" + aBot + ", " + aData + ")");
				Client.SendMessage(SendType.Message, aBot.Name, aData, Priority.Critical);
			}
		}

		void SendData (XG.Core.Server aServer, string aData)
		{
			if (aServer == Server)
			{
				Client.WriteLine(aData);
			}
		}

		#endregion

		#region IRC Stuff

		void RegisterIrcEvents()
		{
			Client.OnPing += (sender, e) => Client.RfcPong(e.Data.Message);

			Client.OnConnected += (sender, e) =>
			{
				Server.Connected = true;
				Server.Commit();
				_log.Info("connected " + Server);

				Client.Login(Settings.Instance.IrcNick, Settings.Instance.IrcNick, 0, Settings.Instance.IrcNick, Settings.Instance.IrcPasswort);

				var channels = (from channel in Server.Channels where channel.Enabled select channel.Name).ToArray();
				Client.RfcJoin(channels);
				Client.Listen();
			};

			Client.OnError += (sender, e) => _log.Info("error from " + Server + ": " + e.ErrorMessage);

			Client.OnConnectionError += (sender, e) => _log.Info("connection error from " + Server + ": " + e);

			Client.OnConnecting += (sender, e) =>
			{
				Server.Connected = false;
				Server.Commit();
				_log.Info("connecting to " + Server);
			};

			Client.OnDisconnected += (sender, e) =>
			{
				Server.Connected = false;
				Server.Commit();
				_log.Info("disconnected " + Server);
				OnDisconnected(Server);
			};

			Client.OnJoin += (sender, e) =>
			{
				var channel = Server.Channel(e.Channel);
				if (channel != null)
				{
					if (_iam == e.Who)
					{
						channel.ErrorCode = 0;
						channel.Connected = true;
						_log.Info("joined " + channel);

						FireNotificationAdded(new Notification(Notification.Types.ChannelJoined, channel));
					}
					else
					{
						var bot = channel.Bot(e.Who);
						if (bot != null)
						{
							bot.Connected = true;
							bot.LastMessage = "joined channel " + channel.Name;
							if (bot.State != Bot.States.Active)
							{
								bot.State = Bot.States.Idle;
							}
							UpdateBot(bot);
							RequestFromBot(bot);
						}
					}
					UpdateChannel(channel);
				}
			};

			Client.OnPart += (sender, e) =>
			{
				var channel = Server.Channel(e.Data.Channel);
				if (channel != null)
				{
					if (_iam == e.Who)
					{
						channel.Connected = false;
						channel.ErrorCode = 0;
						_log.Info("parted " + channel);

						FireNotificationAdded(new Notification(Notification.Types.ChannelParted, channel));
					}
					else
					{
						var bot = channel.Bot(e.Who);
						if (bot != null)
						{
							bot.Connected = false;
							bot.LastMessage = "parted channel " + e.Channel;
							UpdateBot(bot);
						}
					}
					UpdateChannel(channel);
				}
			};

			Client.OnNickChange += (sender, e) =>
			{
				if (_iam == e.OldNickname)
				{
					_iam = e.NewNickname;
				}
				else
				{
					var bot = Server.Bot(e.OldNickname);
					if (bot != null)
					{
						bot.Name = e.NewNickname;
						UpdateBot(bot);
					}
				}
			};

			Client.OnBan += (sender, e) =>
			{
				var channel = Server.Channel(e.Channel);
				if (channel != null)
				{
					if (_iam == e.Who)
					{
						channel.Connected = false;
					}
					else
					{
						var bot = channel.Bot(e.Who);
						if (bot != null)
						{
							bot.Connected = false;
							bot.LastMessage = "banned from " + e.Data.Channel;
							UpdateBot(bot);
						}
					}
					UpdateChannel(channel);
				}
			};

			Client.OnKick += (sender, e) =>
			{
				var channel = Server.Channel(e.Data.Channel);
				if (channel != null)
				{
					if (_iam == e.Whom)
					{
						channel.Connected = false;
						_log.Warn("kicked from " + channel.Name + " (" + e.KickReason + ")");
						FireNotificationAdded(new Notification(Notification.Types.ChannelKicked, channel));
					}
					else
					{
						var bot = channel.Bot(e.Whom);
						if (bot != null)
						{
							bot.Connected = false;
							bot.LastMessage = "kicked from " + e.Channel;
							UpdateBot(bot);
						}
					}
					UpdateChannel(channel);
				}
			};

			Client.OnQuit += (sender, e) =>
			{
				var bot = Server.Bot(e.Who);
				if (bot != null)
				{
					bot.Connected = false;
					bot.LastMessage = "quited";
					UpdateBot(bot);
					UpdateChannel(bot.Parent);
				}
			};

			Client.OnNames += (sender, e) =>
			{
				var channel = Server.Channel(e.Channel);
				if (channel != null)
				{
					foreach (string user in e.UserList)
					{
						var bot = channel.Bot(Regex.Replace(user, "^(@|!|%|\\+){1}", ""));
						if (bot != null)
						{
							bot.Connected = true;
							bot.LastMessage = "joined channel " + channel.Name;
							if (bot.State != Bot.States.Active)
							{
								bot.State = Bot.States.Idle;
							}
							bot.Commit();
							RequestFromBot(bot);
						}
					}
					UpdateChannel(channel);
				}
			};

			Client.OnTopic += (sender, e) =>
			{
				var channel = Server.Channel(e.Channel);
				if (channel != null)
				{
					channel.Topic = Irc.Parser.Helper.RemoveSpecialIrcChars(e.Topic);
					channel.Commit();
				}
			};

			Client.OnTopicChange += (sender, e) =>
			{
				var channel = Server.Channel(e.Channel);
				if (channel != null)
				{
					channel.Topic = Irc.Parser.Helper.RemoveSpecialIrcChars(e.NewTopic);
					channel.Commit();
				}
			};

			Client.OnUnban += (sender, e) =>
			{
				var channel = Server.Channel(e.Channel);
				if (channel != null)
				{
					if (_iam == e.Who)
					{
						channel.ErrorCode = 0;
						channel.Commit();
						AddChannelToQueue(channel, Settings.Instance.CommandWaitTime);
					}
				}
			};

			Client.OnErrorMessage += (sender, e) =>
			{
				var channel = Server.Channel(e.Data.Channel);
				if (channel == null && e.Data.RawMessageArray.Length >= 4)
				{
					channel = Server.Channel(e.Data.RawMessageArray[3]);
				}
				if (channel != null)
				{
					int tWaitTime = 0;
					var notificationType = Notification.Types.ChannelJoinFailed;
					switch (e.Data.ReplyCode)
					{
						case ReplyCode.ErrorNoChannelModes:
						case ReplyCode.ErrorTooManyChannels:
						case ReplyCode.ErrorNotRegistered:
						case ReplyCode.ErrorChannelIsFull:
							tWaitTime = Settings.Instance.ChannelWaitTimeShort;
							break;

						case ReplyCode.ErrorInviteOnlyChannel:
						case ReplyCode.ErrorUniqueOpPrivilegesNeeded:
							tWaitTime = Settings.Instance.ChannelWaitTimeMedium;
							break;

						case ReplyCode.ErrorBannedFromChannel:
							tWaitTime = Settings.Instance.ChannelWaitTimeLong;
							break;
					}
					if (tWaitTime > 0)
					{
						channel.ErrorCode = (int)e.Data.ReplyCode;
						channel.Connected = false;
						_log.Warn("could not join " + channel + ": " + e.Data.ReplyCode);

						FireNotificationAdded(new Notification(notificationType, channel));
						AddChannelToQueue(channel, tWaitTime);
					}

					channel.Commit();
				}
			};

			Client.OnQueryMessage += (sender, e) => Parser.Parse(this, e);

			Client.OnQueryAction += (sender, e) => _log.Debug("OnQueryAction " + e.Data.Message);

			Client.OnChannelMessage += (sender, e) => Parser.Parse(this, e);

			Client.OnChannelNotice += (sender, e) => _log.Debug("OnChannelNotice " + e.Data.Message);

			Client.OnQueryNotice += (sender, e) => Parser.Parse(this, e);

			Client.OnCtcpReply += (sender, e) => Parser.Parse(this, e);

			Client.OnCtcpRequest += (sender, e) => Parser.Parse(this, e);

			Client.OnWriteLine += (sender, e) => _log.Debug("OnWriteLine " + e.Line);
		}

		void UpdateChannel(XG.Core.Channel aChannel)
		{
			var channel = Client.GetChannel(aChannel.Name);
			if (channel != null)
			{
				aChannel.UserCount = channel.Users.Count;
			}
			aChannel.Commit();
		}

		void UpdateBot(Bot aBot)
		{
			// dont hammer plugins with not needed information updates - 60 seconds are enough
			if ((DateTime.Now - aBot.LastContact).TotalSeconds > 60)
			{
				aBot.LastContact = DateTime.Now;
			}
			aBot.Commit();
		}

		void RequestFromBot(Bot aBot)
		{
			if (aBot.State == Bot.States.Idle)
			{
				// check if the packet is already downloaded, or active - than disable it and get the next one
				Packet tPacket = aBot.OldestActivePacket();
				while (tPacket != null)
				{
					Int64 tChunk = FileActions.NextAvailablePartSize(tPacket.RealName != "" ? tPacket.RealName : tPacket.Name,
					                                                 tPacket.RealSize != 0 ? tPacket.RealSize : tPacket.Size);
					if (tChunk == -1)
					{
						_log.Warn("RequestFromBot(" + aBot + ") packet #" + tPacket.Id + " (" + tPacket.Name + ") is already in use");
						tPacket.Enabled = false;
						tPacket = aBot.OldestActivePacket();
					}
					else
					{
						string name = XG.Core.Helper.ShrinkFileName(tPacket.RealName != "" ? tPacket.RealName : tPacket.Name, 0);
						if (_latestPacketRequests.ContainsKey(name))
						{
							double time = (_latestPacketRequests[name] - DateTime.Now).TotalSeconds;
							if (time > 0)
							{
								_log.Warn("RequestFromBot(" + aBot + ") packet name " + tPacket.Name + " is blocked for " + time + "ms");
								AddBotToQueue(aBot, (int) time + 1);
								return;
							}
						}

						if (_server.Connected)
						{
							_log.Info("RequestFromBot(" + aBot + ") requesting packet #" + tPacket.Id + " (" + tPacket.Name + ")");
							Client.SendMessage(SendType.Message, aBot.Name, "XDCC SEND " + tPacket.Id, Priority.Critical);

							if (_latestPacketRequests.ContainsKey(name))
							{
								_latestPacketRequests.Remove(name);
							}
							_latestPacketRequests.Add(name, DateTime.Now.AddSeconds(Settings.Instance.SamePacketRequestTime));

							FireNotificationAdded(new Notification(Notification.Types.PacketRequested, tPacket));
						}

						// create a timer to re request if the bot didnt recognized the privmsg
						AddBotToQueue(aBot, Settings.Instance.BotWaitTime);
						break;
					}
				}
			}
		}

		void UnRequestFromBot(Bot aBot)
		{
			_log.Info("UnRequestFromBot(" + aBot + ")");
			Client.SendMessage(SendType.Message, aBot.Name, "XDCC REMOVE", Priority.Critical);

			AddBotToQueue(aBot, Settings.Instance.CommandWaitTime);

			FireNotificationAdded(new Notification(Notification.Types.PacketRemoved, aBot));
		}

		#endregion

		#region AWorker

		protected override void StartRun()
		{
			_iam = Settings.Instance.IrcNick;

			Client = new IrcClient()
			{
				AutoNickHandling = true,
				ActiveChannelSyncing = true,
				AutoReconnect = true,
				AutoRetry = true,
				AutoJoinOnInvite = true,
				AutoRejoinOnKick = true,
				CtcpVersion = Settings.Instance.IrcVersion
			};

			RegisterIrcEvents();

			try
			{
				Client.Connect(Server.Name, Server.Port);
			}
			catch(CouldNotConnectException ex)
			{
				_log.Fatal("StartRun() connection failed " + ex.Message);
				Server.Connected = false;
				Server.Commit();
				OnDisconnected(Server);
			}
		}

		protected override void StopRun()
		{
			try
			{
				Client.Disconnect();
			}
			catch (NotConnectedException)
			{
				// this is ok
			}
		}

		#endregion

		#region TIMER

		public void TriggerTimerRun()
		{
			TriggerChannelRun();
			TriggerBotRun();
		}

		void TriggerChannelRun()
		{
			var remove = new HashSet<XG.Core.Channel>();

			foreach (var kvp in _channelQueue)
			{
				DateTime time = kvp.Value;
				if ((time - DateTime.Now).TotalSeconds < 0)
				{
					remove.Add(kvp.Key);
				}
			}

			foreach (var channel in remove)
			{
				_channelQueue.Remove(channel);

				Client.RfcJoin(channel.Name);
			}
		}

		void TriggerBotRun()
		{
			var remove = new HashSet<Bot>();

			foreach (var kvp in _botQueue)
			{
				DateTime time = kvp.Value;
				if ((time - DateTime.Now).TotalSeconds < 0)
				{
					remove.Add(kvp.Key);
				}
			}

			foreach (Bot bot in remove)
			{
				_botQueue.Remove(bot);
				RequestFromBot(bot);
			}
		}

		public void AddBotToQueue(Bot aBot, int aInt)
		{
			if (!_botQueue.ContainsKey(aBot))
			{
				_botQueue.Add(aBot, DateTime.Now.AddSeconds(aInt));
			}
		}

		public void AddChannelToQueue(XG.Core.Channel aChannel, int aInt)
		{
			if (!_channelQueue.ContainsKey(aChannel))
			{
				_channelQueue.Add(aChannel, DateTime.Now.AddSeconds(aInt));
			}
		}

		#endregion
	}
}
