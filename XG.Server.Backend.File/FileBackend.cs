//
//  Copyright (C) 2012 Lars Formella <ich@larsformella.de>
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
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using XG.Core;

namespace XG.Server.Backend.File
{
	public class FileBackend : IServerPlugin
	{
		#region VARIABLES

		private ServerRunner myRunner;

		private BinaryFormatter myFormatter = new BinaryFormatter();

		private Thread mySaveDataThread;

		private bool isSaveFile = false;
		private object mySaveFileLock = new object();

		private object mySaveSearchLock = new object();

		#endregion

		public FileBackend (ServerRunner aParent)
		{
			this.myRunner = aParent;

			// the one and only root object
			this.myRunner.myRootObject = (RootObject)this.Load(Settings.Instance.DataBinary);
			if (this.myRunner.myRootObject == null) { this.myRunner.myRootObject = new RootObject(); }

			// the file data
			this.myRunner.myFiles = (List<XGFile>)this.Load(Settings.Instance.FilesBinary);
			if (this.myRunner.myFiles == null) { this.myRunner.myFiles = new List<XGFile>(); }

			// previous searches
			this.myRunner.mySearches = (List<string>)this.Load(Settings.Instance.SearchesBinary);
			if (this.myRunner.mySearches == null) { this.myRunner.mySearches = new List<string>(); }
		}

		#region RUN STOP

		public void Start (ServerRunner aParent)
		{
			// start data saving routine
			this.mySaveDataThread = new Thread(new ThreadStart(SaveDataLoop));
			this.mySaveDataThread.Start();

			this.myRunner.ObjectAddedEvent += new ObjectObjectDelegate (myRunner_ObjectAddedEventHandler);
			this.myRunner.ObjectChangedEvent += new ObjectDelegate (myRunner_ObjectChangedEventHandler);
			this.myRunner.ObjectRemovedEvent += new ObjectObjectDelegate (myRunner_ObjectRemovedEventHandler);

			this.myRunner.SearchAddedEvent += new DataTextDelegate (myRunner_SearchAddedEvent);
			this.myRunner.SearchRemovedEvent += new DataTextDelegate (myRunner_SearchRemovedEvent);
		}

		public void Stop ()
		{
			this.myRunner.ObjectAddedEvent -= new ObjectObjectDelegate (myRunner_ObjectAddedEventHandler);
			this.myRunner.ObjectChangedEvent -= new ObjectDelegate (myRunner_ObjectChangedEventHandler);
			this.myRunner.ObjectRemovedEvent -= new ObjectObjectDelegate (myRunner_ObjectRemovedEventHandler);

			this.myRunner.SearchAddedEvent -= new DataTextDelegate (myRunner_SearchAddedEvent);
			this.myRunner.SearchRemovedEvent -= new DataTextDelegate (myRunner_SearchRemovedEvent);

			this.mySaveDataThread.Abort();
		}
		
		#endregion

		#region EVENTS

		private void myRunner_ObjectAddedEventHandler (XGObject aParentObj, XGObject aObj)
		{
			// we are just interested in files or fileparts
			if (aObj.GetType() == typeof(XGFile) || aObj.GetType() == typeof(XGFilePart))
			{
				// to save em now
				this.SaveFileDataNow();
			}
		}

		private void myRunner_ObjectChangedEventHandler (XGObject aObj)
		{
			if (aObj.GetType() == typeof(XGFile))
			{
				this.SaveFileDataNow();
			}
			else if (aObj.GetType() == typeof(XGFilePart))
			{
				XGFilePart part = aObj as XGFilePart;
				// if this change is lost, the data might be corrupt, so save it NOW
				if (part.PartState != FilePartState.Open)
				{
					this.SaveFileDataNow();
				}
				// the data saving can be scheduled
				else
				{
					this.isSaveFile = true;
				}
			}
		}

		private void myRunner_ObjectRemovedEventHandler (XGObject aParentObj, XGObject aObj)
		{
			// we are just interested in files or fileparts
			if (aObj.GetType() == typeof(XGFile) || aObj.GetType() == typeof(XGFilePart))
			{
				// to save em now
				this.SaveFileDataNow();
			}
		}

		private void myRunner_SearchAddedEvent(string aSearch)
		{
			lock (this.mySaveSearchLock)
			{
				this.Save(this.myRunner.mySearches, Settings.Instance.SearchesBinary);
			}
		}

		private void myRunner_SearchRemovedEvent(string aSearch)
		{
			lock (this.mySaveSearchLock)
			{
				this.Save(this.myRunner.mySearches, Settings.Instance.SearchesBinary);
			}
		}

		#endregion

		#region SAVE + LOAD

		/// <summary>
		/// Serializes an object into a file
		/// </summary>
		/// <param name="aObj"></param>
		/// <param name="aFile"></param>
		private void Save(object aObj, string aFile)
		{
			try
			{
				Stream streamWrite = System.IO.File.Create(aFile + ".new");
				this.myFormatter.Serialize(streamWrite, aObj);
				streamWrite.Close();
				try { System.IO.File.Delete(aFile + ".bak"); }
				catch (Exception) { };
				try { System.IO.File.Move(aFile, aFile + ".bak"); }
				catch (Exception) { };
				System.IO.File.Move(aFile + ".new", aFile);
				this.Log("Save(" + aFile + ")", LogLevel.Info);
			}
			catch (Exception ex)
			{
				this.Log("Save(" + aFile + ") : " + XGHelper.GetExceptionMessage(ex), LogLevel.Exception);
			}
		}

		/// <summary>
		/// Deserializes an object from a file
		/// </summary>
		/// <param name="aFile">Name of the File</param>
		/// <returns>the object or null if the deserializing failed</returns>
		private object Load(string aFile)
		{
			object obj = null;
			if (System.IO.File.Exists(aFile))
			{
				try
				{
					Stream streamRead = System.IO.File.OpenRead(aFile);
					obj = this.myFormatter.Deserialize(streamRead);
					streamRead.Close();
					this.Log("Load(" + aFile + ")", LogLevel.Info);
				}
				catch (Exception ex)
				{
					this.Log("Load(" + aFile + ") : " + XGHelper.GetExceptionMessage(ex), LogLevel.Exception);
					// try to load the backup
					try
					{
						Stream streamRead = System.IO.File.OpenRead(aFile + ".bak");
						obj = this.myFormatter.Deserialize(streamRead);
						streamRead.Close();
						this.Log("Load(" + aFile + ".bak)", LogLevel.Info);
					}
					catch (Exception)
					{
						this.Log("Load(" + aFile + ".bak) : " + XGHelper.GetExceptionMessage(ex), LogLevel.Exception);
					}
				}
			}
			return obj;
		}

		private void SaveDataLoop()
		{
			DateTime timeIrc = DateTime.Now;
			DateTime timeStats = DateTime.Now;

			while (true)
			{
				// IRC Data
				if ((DateTime.Now - timeIrc).TotalMilliseconds > Settings.Instance.BackupDataTime)
				{
					timeIrc = DateTime.Now;

					RootObject tObj = new RootObject();
					tObj.Clone(this.myRunner.myRootObject, false);

					foreach (XGServer oldServ in this.myRunner.myRootObject.Children)
					{
						XGServer newServ = new XGServer(tObj);
						newServ.Clone(oldServ, false);
						foreach (XGChannel oldChan in oldServ.Children)
						{
							XGChannel newChan = new XGChannel(newServ);
							newChan.Clone(oldChan, false);
							foreach (XGBot oldBot in oldChan.Children)
							{
								XGBot newBot = new XGBot(newChan);
								newBot.Clone(oldBot, false);
								foreach (XGPacket oldPack in oldBot.Children)
								{
									XGPacket newPack = new XGPacket(newBot);
									newPack.Clone(oldPack, false);
								}
							}
						}
					}

					this.Save(tObj, Settings.Instance.DataBinary);

					tObj = null;
				}

				// File Data
				if (this.isSaveFile)
				{
					lock (this.mySaveFileLock)
					{
						this.Save(this.myRunner.myFiles, Settings.Instance.FilesBinary);
						this.isSaveFile = false;
					}
				}

				// Statistics
				if ((DateTime.Now - timeStats).TotalMilliseconds > Settings.Instance.BackupStatisticTime)
				{
					timeStats = DateTime.Now;
					Statistic.Instance.Save();
				}

				Thread.Sleep((int)Settings.Instance.TimerSleepTime);
			}
		}

		/// <summary>
		/// Save the FileData right now 
		/// </summary>
		private void SaveFileDataNow()
		{
			lock (this.mySaveFileLock)
			{
				this.Save(this.myRunner.myFiles, Settings.Instance.FilesBinary);
				this.isSaveFile = false;
			}
		}

		#endregion

		#region LOG

		/// <summary>
		/// Calls  XGHelper.Log()
		/// </summary>
		/// <param name="aData"></param>
		/// <param name="aLevel"></param>
		private void Log(string aData, LogLevel aLevel)
		{
			XGHelper.Log("FileBackend." + aData, aLevel);
		}

		#endregion
	}
}

