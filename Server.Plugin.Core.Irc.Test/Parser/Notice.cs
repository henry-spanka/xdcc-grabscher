// 
//  Notice.cs
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

using NUnit.Framework;
using Meebey.SmartIrc4net;

namespace XG.Server.Plugin.Core.Irc.Parser.Test
{
	[TestFixture]
	public class Notice : AParser
	{
		//Irc.Parser.Notice _notice = new Irc.Parser.Notice();

		public Notice()
		{
			/*_notice.OnJoinChannel += (aServer, aData) => {
				EventData = aData;
			};*/
		}

		[Test]
		public void BotMessages()
		{
			//_notice.Parse(Server, ":[XG]TestBot!~SYSTEM@XG.BITPIR.AT NOTICE xg1_bitpir_at : ** Closing Connection You Must JOIN MG-CHAT As Well To Download - Your Download Will Be Canceled Now");
			Assert.AreEqual("MG-CHAT", EventData);
		}
	}
}
