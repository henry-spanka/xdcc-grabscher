//
//  main.js
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

var XG;
var XGMain = Class.create(
{
	/**
	 * @param {XGHelper} helper
	 * @param {XGStatistics} statistics
	 * @param {XGCookie} cookie
	 * @param {XGFormatter} formatter
	 * @param {XGWebsocket} websocket
	 * @param {XGDataView} repository
	 * @param {XGGrid} grid
	 * @param {XGResize} resize
	 */
	initialize: function(helper, statistics, cookie, formatter, websocket, repository, grid, resize)
	{
		XG = this;
		var self = this;

		this.statistics = statistics;
		this.cookie = cookie;
		this.helper = helper;
		this.formatter = formatter;
		this.websocket = websocket;
		this.dataview = repository;
		this.grid = grid;
		this.resize = resize;

		this.idServer = 0;
		this.activeTab = 0;
	},

	start: function()
	{
		var self = this;

		// socket
		this.websocket.onAdd.subscribe(function (e, args) {
			self.dataview.addItem(args);
		});
		this.websocket.onRemove.subscribe(function (e, args) {
			self.dataview.removeItem(args);
		});
		this.websocket.onUpdate.subscribe(function (e, args) {
			self.dataview.updateItem(args);
		});
		this.websocket.onSearches.subscribe(function (e, args) {
			self.dataview.setItems(args);
		});
		this.websocket.connect();

		// grid
		this.resize.onResize.subscribe(function (e, args) {
			self.grid.resize();
			self.statistics.updateSnapshotPlot();
		});
		this.grid.onClick.subscribe(function (e, args) {
			switch (args.grid)
			{
				case "serverGrid":
					self.websocket.sendGuid(Enum.Request.ChannelsFromServer, args.object.Guid);
					break;

				case "botGrid":
					self.websocket.sendGuid(Enum.Request.PacketsFromBot, args.object.Guid);
					break;

				case "searchGrid":
					self.websocket.sendGuid(Enum.Request.Search, args.object.Guid);
					break;
			}
		});
		this.grid.build();

		// resize
		this.resize.start();

		// other
		this.initializeDialogs();
		this.initializeOthers();
	},

	initializeDialogs: function()
	{
		var self = this;

		/* ********************************************************************************************************** */
		/* SERVER / CHANNEL DIALOG                                                                                    */
		/* ********************************************************************************************************** */

		$("#serverChannelButton")
			.button({icons: { primary: "icon-globe-1" }})
			.click( function()
			{
				$("#dialogServerChannels").dialog("open");
			});

		$("#dialogServerChannels").dialog({
			autoOpen: false,
			width: 830,
			modal: true,
			resizable: false
		});

		/* ********************************************************************************************************** */
		/* STATISTICS DIALOG                                                                                          */
		/* ********************************************************************************************************** */

		$("#statisticsButton")
			.button({icons: { primary: "icon-chart-bar" }})
			.click( function()
			{
				self.websocket.send(Enum.Request.Statistics);
				$("#dialogStatistics").dialog("open");
			});

		$("#dialogStatistics").dialog({
			autoOpen: false,
			width: 545,
			modal: true,
			resizable: false
		});

		/* ********************************************************************************************************** */
		/* SNAPSHOTS DIALOG                                                                                           */
		/* ********************************************************************************************************** */

		//$(".snapshotCheckbox").button();
		$(".snapshotCheckbox, input[name='snapshotTime']").click( function()
		{
			self.statistics.updateSnapshotPlot();
		});

		$("#snapshotsButton")
			.button({icons: { primary: "icon-chart-bar" }})
			.click( function()
			{
				$("#dialogSnapshots").dialog("open");
			});

		$("#dialogSnapshots").dialog({
			autoOpen: false,
			width: $(window).width() - 20,
			height: $(window).height() - 20,
			modal: true,
			resizable: false
		});

		/* ********************************************************************************************************** */
		/* ERROR DIALOG                                                                                               */
		/* ********************************************************************************************************** */

		$("#dialogError").dialog({
			autoOpen: false,
			modal: true,
			resizable: false,
			close: function()
			{
				$('#dialogError').dialog('open');
			}
		});
	},

	initializeOthers: function()
	{
		var self = this;

		$("#tabs").tabs({
			select: function(event, ui)
			{
				self.activeTab = ui.index;
			}
		});
		$("#showOfflineBots")
			.button({icons: { primary: "icon-eye" }})
			.click( function()
			{
				self.cookie.setCookie("showOfflineBots", $("#showOfflineBots").attr('checked') ? "1" : "0" );
			});
		$("#humanDates")
			.button({icons: { primary: "icon-clock" }})
			.click( function()
			{
				self.cookie.setCookie("humanDates", $("#humanDates").attr('checked') ? "1" : "0" );
			});
	},

	addSearch: function ()
	{
		var tbox = $('#search-text');
		if(tbox.val() != "")
		{
			this.websocket.sendName(Enum.Request.AddSearch, tbox.val());
			tbox.val('');
		}
	},

	/**
	 * @param {String} guid
	 */
	removeSearch: function (guid)
	{
		this.websocket.sendGuid(Enum.Request.RemoveSearch, guid);
	},

	/**
	 * @param {String} guid
	 */
	flipPacket: function (guid)
	{
		var self = this;

		var pack = self.websocket.getRowData('packets_table', guid);
		if(pack)
		{
			if(!pack.Enabled)
			{
				$("#" + pack.Guid).effect("transfer", { to: $("#00000000-0000-0000-0000-000000000004") }, 500);
			}
			else
			{
				$("#00000000-0000-0000-0000-000000000004").effect("transfer", { to: $("#" + pack.Guid) }, 500);
			}
			self.flipObject(guid, "packets_table");
		}
	},

	/**
	 * @param {String} guid
	 * @param {String} grid
	 */
	flipObject: function (guid, grid)
	{
		var self = this;

		var obj = self.websocket.getRowData(grid, guid);
		if(obj)
		{
			if(!obj.Enabled)
			{
				self.websocket.sendGuid(Enum.Request.ActivateObject, obj.Guid);
			}
			else
			{
				self.websocket.sendGuid(Enum.Request.DeactivateObject, obj.Guid);
			}
		}
	},

	/**
	 * @param {String} guid
	 */
	downloadLink: function (guid)
	{
		var self = XG;

		$("#" + guid).effect("transfer", { to: $("#00000000-0000-0000-0000-000000000004") }, 500);

		var data = self.websocket.getRowData("packets_external_table", guid);
		self.websocket.sendName(Enum.Request.ParseXdccLink, data.IrcLink);
	}
});
