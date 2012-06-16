//  
//  Copyright (C) 2009 Lars Formella <ich@larsformella.de>
// 
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
// 
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;

namespace XG.Core
{
	[Serializable()]
	public class XGObject
	{
		#region EVENTS

		[field: NonSerialized()]
		public event ObjectDelegate EnabledChangedEvent;

		[field: NonSerialized()]
		public object locked = new object();

		#endregion

		#region VARIABLES

		private Guid parentGuid;
		public Guid ParentGuid
		{
			get { return parentGuid; }
		}

		private XGObject parent;
		public XGObject Parent
		{
			get { return this.parent; }
			set
			{
				if (this.parent != value)
				{
					this.parent = value;
					if (this.parent != null) { this.parentGuid = this.parent.Guid; }
					else { this.parentGuid = Guid.Empty; }
				}
			}
		}

		public Guid Guid
		{
			get;
			set;
		}

		private bool connected;
		public virtual bool Connected
		{
			get { return this.connected; }
			set
			{
				if (this.connected != value)
				{
					this.connected = value;
					this.Modified = true;
				}
			}
		}

		private bool enabled;
		public bool Enabled
		{
			get { return this.enabled; }
			set
			{
				if (this.enabled != value)
				{
					// disable all children
					if (!value)
					{
						foreach (XGObject tObj in this.Children)
						{
							tObj.Enabled = value;
						}
					}

					this.enabled = value;

					// just set the time if this object is enabled
					if (this.enabled)
					{
						this.lastModified = DateTime.Now;
					}
					if (this.EnabledChangedEvent != null)
					{
						this.EnabledChangedEvent(this);
					}
				}
			}
		}

		private string name;
		public string Name
		{
			get { return this.name; }
			set
			{
				if (this.name != value)
				{
					this.name = value;
					this.Modified = true;
				}
			}
		}

		private DateTime lastModified = new DateTime(1, 1, 1);
		public DateTime LastModified
		{
			get { return this.lastModified; }
		}

		private bool modified;
		public bool Modified
		{
			get { return this.modified; }
			set { this.modified = value; }
		}

		#endregion

		#region CHILDREN

		private List<XGObject> children;
		protected IEnumerable<XGObject> Children
		{
			get { return this.children.ToArray(); }
		}

		protected bool AddChild(XGObject aObject)
		{
			if (aObject != null)
			{
				if (!this.children.Contains(aObject))
				{
					XGObject tObj = this.GetChildByGuid(aObject.Guid);
					if (tObj != null)
					{
						XGHelper.CloneObject(aObject, tObj, true);
					}
					else
					{
						this.children.Add(aObject);
						aObject.Parent = this;
						return true;
					}
				}
			}
			return false;
		}

		protected bool RemoveChild(XGObject aObject)
		{
			if (aObject != null)
			{
				if (this.children.Contains(aObject))
				{
					this.children.Remove(aObject);
					return true;
				}
			}
			return false;
		}

		public XGObject GetChildByGuid(Guid aGuid)
		{
			if (aGuid == Guid.Empty) { return null; }
			if (this.Guid == aGuid) { return this; }

			XGObject tObj = null;
			foreach (XGObject o in this.Children)
			{
				if (o.Guid == aGuid)
				{
					tObj = o;
					break;
				}
				else
				{
					tObj = o.GetChildByGuid(aGuid);
					if (tObj != null) { break; }
				}
			}
			return tObj;
		}

		public XGObject GetNextChild(XGObject aObject)
		{
			if (this.children.Contains(aObject))
			{
				bool next = false;
				foreach (XGObject tObj in this.Children)
				{
					if (tObj == aObject) { next = true; }
					else if (next) { return tObj; }
				}
			}
			return null;
		}

		#endregion

		#region CONSTRUCTOR

		public XGObject()
		{
			this.name = "";
			this.children = new List<XGObject>();
			this.Guid = Guid.NewGuid();
			this.connected = false;
			this.enabled = false;
		}

		public void Clone(XGObject aCopy, bool aFull)
		{
			this.parentGuid = aCopy.parentGuid;
			this.Guid = aCopy.Guid;
			this.name = aCopy.name;
			this.enabled = aCopy.enabled;
			this.lastModified = aCopy.lastModified;
			if(aFull)
			{
				this.connected = aCopy.connected;
			}
		}

		#endregion
	}
}
