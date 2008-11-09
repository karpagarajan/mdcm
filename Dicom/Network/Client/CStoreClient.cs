﻿// mDCM: A C# DICOM library
//
// Copyright (c) 2006-2008  Colby Dillion
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
//
// Author:
//    Colby Dillion (colby.dillion@gmail.com)

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Dicom;
using Dicom.Codec;
using Dicom.Data;
using Dicom.Network;
using Dicom.Utility;

namespace Dicom.Network.Client {
	/// <summary>
	/// C-Store Request Information
	/// </summary>
	public class CStoreRequestInfo : IPreloadable<CStoreClient> {
		#region Private Members
		private bool _loaded;
		private string _fileName;
		private DcmTS _transferSyntax;
		private DcmTS _originalTransferSyntax;
		private DcmDataset _dataset;
		private Exception _exception;
		private object _userState;
		private DcmStatus _status;
		private DcmUID _sopClass;
		private DcmUID _sopInst;
		#endregion

		#region Public Constructors
		public CStoreRequestInfo(string fileName) : this(fileName, null) {
		}

		public CStoreRequestInfo(string fileName, object userModel) {
			try {
				_fileName = fileName;
				if (!File.Exists(fileName))
					throw new FileNotFoundException("Unable to load DICOM file!", fileName);

				DcmTag stopTag = (userModel != null) ? DcmTags.PixelData : DcmFileMetaInfo.StopTag;
				DicomFileFormat ff = new DicomFileFormat();
				ff.Load(fileName, stopTag, DicomReadOptions.Default);
				_transferSyntax = ff.FileMetaInfo.TransferSyntax;
				_originalTransferSyntax = _transferSyntax;
				_sopClass = ff.FileMetaInfo.MediaStorageSOPClassUID;
				_sopInst = ff.FileMetaInfo.MediaStorageSOPInstanceUID;
				if (userModel != null) {
					ff.Dataset.LoadDicomFields(userModel);
					_userState = userModel;
				}
				_status = DcmStatus.Pending;
			}
			catch (Exception e) {
				_status = DcmStatus.ProcessingFailure;
				_exception = e;
				throw;
			}
		}
		#endregion

		#region Public Properties
		public bool IsLoaded {
			get { return _loaded; }
		}

		public string FileName {
			get { return _fileName; }
		}

		public DcmUID SOPClassUID {
			get { return _sopClass; }
		}

		public DcmUID SOPInstanceUID {
			get { return _sopInst; }
		}

		public DcmTS TransferSyntax {
			get { return _transferSyntax; }
		}

		public bool HasError {
			get { return _exception != null; }
		}

		public Exception Error {
			get { return _exception; }
		}

		public DcmStatus Status {
			get { return _status; }
			internal set { _status = value; }
		}

		public object UserState {
			get { return _userState; }
			set { _userState = value; }
		}
		#endregion

		#region Public Methods
		/// <summary>
		/// Loads the DICOM file and changes the transfer syntax if needed. (Internal)
		/// </summary>
		/// <param name="client">C-Store Client</param>
		public void Load(CStoreClient client) {
			if (_loaded)
				return;

			try {
				DcmTS tx = null;

				foreach (DcmPresContext pc in client.Associate.GetPresentationContexts()) {
					if (pc.Result == DcmPresContextResult.Accept && pc.AbstractSyntax == _sopClass) {
						tx = pc.AcceptedTransferSyntax;
						break;
					}
				}

				if (tx == null)
					throw new DcmNetworkException("No accepted presentation contexts for abstract syntax: " + _sopClass.Description);

				// Possible to stream from file?
				if (!client.DisableFileStreaming && tx == TransferSyntax)
					return;

				DcmCodecParameters codecParams = null;
				if (tx == client.PreferredTransferSyntax)
					codecParams = client.PreferredTransferSyntaxParams;

				DicomFileFormat ff = new DicomFileFormat();
				ff.Load(FileName, DicomReadOptions.Default);

				if (_originalTransferSyntax != tx) {
					if (_originalTransferSyntax.IsEncapsulated) {
						// Dataset is compressed... decompress
						try {
							ff.ChangeTransferSytnax(DcmTS.ExplicitVRLittleEndian, null);
						}
						catch {
							client.Log.Error("{0} -> Unable to change transfer syntax:\n\tclass: {1}\n\told: {2}\n\tnew: {3}\n\treason: {4}\n\tcodecs: {5} - {6}",
								client.LogID, SOPClassUID.Description, _originalTransferSyntax, DcmTS.ExplicitVRLittleEndian,
#if DEBUG
								HasError ? "Unknown" : Error.ToString(),
#else
								HasError ? "Unknown" : Error.Message,
#endif
								DicomCodec.HasCodec(_originalTransferSyntax), DicomCodec.HasCodec(DcmTS.ExplicitVRLittleEndian));
							throw;
						}
					}

					if (tx.IsEncapsulated) {
						// Dataset needs to be compressed
						try {
							ff.ChangeTransferSytnax(tx, codecParams);
						}
						catch {
							client.Log.Error("{0} -> Unable to change transfer syntax:\n\tclass: {1}\n\told: {2}\n\tnew: {3}\n\treason: {4}\n\tcodecs: {5} - {6}",
								client.LogID, SOPClassUID.Description, ff.Dataset.InternalTransferSyntax, tx,
#if DEBUG
								HasError ? "Unknown" : Error.ToString(),
#else
								HasError ? "Unknown" : Error.Message,
#endif
								DicomCodec.HasCodec(ff.Dataset.InternalTransferSyntax), DicomCodec.HasCodec(tx));
							throw;
						}
					}
				}

				_dataset = ff.Dataset;
				_transferSyntax = tx;
			}
			catch (Exception e) {
				_dataset = null;
				_transferSyntax = _originalTransferSyntax;
				_status = DcmStatus.ProcessingFailure;
				_exception = e;
			}
			finally {
				_loaded = true;
			}
		}

		/// <summary>
		/// Unloads dataset from memory. (Internal)
		/// </summary>
		public void Unload() {
			_dataset = null;
			_transferSyntax = _originalTransferSyntax;
			_loaded = false;
		}

		/// <summary>
		/// Unloads the dataset and clears any errors. (Internal)
		/// </summary>
		public void Reset() {
			Unload();
			_status = DcmStatus.Pending;
			_exception = null;
		}

		internal bool Send(CStoreClient client) {
			Load(client);

			if (HasError) {
				if (client.Associate.FindAbstractSyntax(SOPClassUID) == 0) {
					client.Reassociate();
					return false;
				}
			}

			if (client.OnCStoreRequestBegin != null)
				client.OnCStoreRequestBegin(client, this);

			if (HasError) {
				if (client.OnCStoreRequestFailed != null)
					client.OnCStoreRequestFailed(client, this);
				return false;
			}

			byte pcid = client.Associate.FindAbstractSyntaxWithTransferSyntax(SOPClassUID, TransferSyntax);

			if (pcid == 0) {
				client.Log.Info("{0} -> C-Store request failed: No accepted presentation context for {1}", client.LogID, SOPClassUID.Description);
				Status = DcmStatus.SOPClassNotSupported;
				if (client.OnCStoreRequestFailed != null)
					client.OnCStoreRequestFailed(client, this);
				return false;
			}

			if (_dataset != null) {
				client.SendCStoreRequest(pcid, SOPInstanceUID, _dataset);
			}
			else {
				using (Stream s = DicomFileFormat.GetDatasetStream(FileName)) {
					client.SendCStoreRequest(pcid, SOPInstanceUID, s);
				}
			}

			if (client.OnCStoreRequestComplete != null)
				client.OnCStoreRequestComplete(client, this);

			return true;
		}
		#endregion
	}

	public delegate void CStoreClientCallback(CStoreClient client);
	public delegate void CStoreRequestCallback(CStoreClient client, CStoreRequestInfo info);
	public delegate void CStoreRequestProgressCallback(CStoreClient client, CStoreRequestInfo info, DcmDimseProgress progress);

	/// <summary>
	/// DICOM C-Store SCU
	/// </summary>
	public class CStoreClient : DcmClientBase {
		#region Private Members
		private int _preloadCount = 1;
		private PreloadQueue<CStoreRequestInfo, CStoreClient> _sendQueue;
		private CStoreRequestInfo _current;
		private DcmTS _preferredTransferSyntax;
		private DcmCodecParameters _preferedSyntaxParams;
		private bool _disableFileStream = false;
		private bool _serialPresContexts = false;
		private int _linger = 0;
		private Dictionary<DcmUID, List<DcmTS>> _presContextMap = new Dictionary<DcmUID, List<DcmTS>>();
		private object _lock = new object();
		private bool _cancel = false;
		private bool _offerExplicit = false;
		#endregion

		#region Public Constructors
		public CStoreClient() : base() {
			CallingAE = "STORE_SCU";
			CalledAE = "STORE_SCP";
			_sendQueue = new PreloadQueue<CStoreRequestInfo, CStoreClient>(this);
			_current = null;
		}
		#endregion

		#region Public Properties
		public CStoreRequestCallback OnCStoreRequestBegin;
		public CStoreRequestCallback OnCStoreRequestFailed;
		public CStoreRequestProgressCallback OnCStoreRequestProgress;
		public CStoreRequestCallback OnCStoreRequestComplete;
		public CStoreRequestCallback OnCStoreResponseReceived;

		public CStoreClientCallback OnCStoreComplete;
		public CStoreClientCallback OnCStoreClosed;

		/// <summary>
		/// First transfer syntax proposed in association.  Used if accepted.
		/// </summary>
		public DcmTS PreferredTransferSyntax {
			get { return _preferredTransferSyntax; }
			set { _preferredTransferSyntax = value; }
		}

		/// <summary>
		/// Codec parameters for the preferred transfer syntax.
		/// </summary>
		public DcmCodecParameters PreferredTransferSyntaxParams {
			get { return _preferedSyntaxParams; }
			set { _preferedSyntaxParams = value; }
		}

		/// <summary>
		/// Create a unique presentation context for each combination of abstract and transfer syntaxes.
		/// </summary>
		public bool SerializedPresentationContexts {
			get { return _serialPresContexts; }
			set { _serialPresContexts = value; }
		}

		/// <summary>
		/// Set to true to force DICOM datasets to be loaded into memory.
		/// </summary>
		public bool DisableFileStreaming {
			get { return _disableFileStream; }
			set { _disableFileStream = value; }
		}

		/// <summary>
		/// Propose Explicit VR Little Endian for all presentation contexts
		/// </summary>
		public bool OfferExplicitSyntax {
			get { return _offerExplicit; }
			set { _offerExplicit = value; }
		}

		/// <summary>
		/// Number of requests to keep preloaded in memory.
		/// </summary>
		public int PreloadCount {
			get { return _preloadCount; }
			set { _preloadCount = value; }
		}

		/// <summary>
		/// Time to keep association alive after sending last image in queue.
		/// </summary>
		public int Linger {
			get { return _linger; }
			set { _linger = value; }
		}

		/// <summary>
		/// Number of pending DICOM files to be sent.
		/// </summary>
		public int PendingCount {
			get {
				if (_current != null)
					return _sendQueue.Count + 1;
				return _sendQueue.Count;
			}
		}
		#endregion

		#region Public Methods
		/// <summary>
		/// Enqueues a file to be transfered to the remote DICOM node.
		/// </summary>
		/// <param name="fileName">File containing DICOM dataset.</param>
		/// <returns>C-Store Request Information</returns>
		public CStoreRequestInfo AddFile(string fileName) {
			return AddFile(fileName, null);
		}

		/// <summary>
		/// Enqueues a file to be transfered to the remote DICOM node.
		/// </summary>
		/// <param name="fileName">File containing DICOM dataset.</param>
		/// <param name="userModel">
		/// User class containing one or more properties marked with a <see cref="Dicom.Data.DicomFieldAttribute"/>.
		/// This object will be stored in the UserState property of the CStoreRequestInfo object.
		/// </param>
		/// <returns>C-Store Request Information</returns>
		public CStoreRequestInfo AddFile(string fileName, object userModel) {
			CStoreRequestInfo info = new CStoreRequestInfo(fileName, userModel);
			AddFile(info);
			return info;
		}

		/// <summary>
		/// Enqueues a file to be transfered to the remote DICOM node.
		/// </summary>
		/// <param name="info">C-Store Request Information</param>
		public void AddFile(CStoreRequestInfo info) {
			if (info.HasError)
				return;

			lock (_lock) {
				_sendQueue.Enqueue(info);
				if (!_presContextMap.ContainsKey(info.SOPClassUID)) {
					_presContextMap.Add(info.SOPClassUID, new List<DcmTS>());
				}
				if (!_presContextMap[info.SOPClassUID].Contains(info.TransferSyntax)) {
					_presContextMap[info.SOPClassUID].Add(info.TransferSyntax);
				}
			}

			if (Linger > 0 && IsClosed && CanReconnect && !ClosedOnError)
				Reconnect();
		}

		/// <summary>
		/// Cancels all pending C-Store requests and releases the association.
		/// 
		/// Call Reconnect() to resume transfer.
		/// </summary>
		/// <param name="wait">
		/// If true the command will wait for the current C-Store operation to complete 
		/// and the association to be released.
		/// </param>
		public void Cancel(bool wait) {
			_cancel = true;
			if (!IsClosed) {
				if (wait)
					Wait();
				else
					Close();
			}
		}
		#endregion

		#region Protected Methods
		protected override void OnConnected() {
			if (PendingCount > 0) {
				DcmAssociate associate = new DcmAssociate();

				lock (_lock) {
					foreach (DcmUID uid in _presContextMap.Keys) {
						if (_preferredTransferSyntax != null) {
							if (!_presContextMap[uid].Contains(_preferredTransferSyntax))
								_presContextMap[uid].Remove(_preferredTransferSyntax);
							_presContextMap[uid].Insert(0, _preferredTransferSyntax);
						}
						if (_offerExplicit && !_presContextMap[uid].Contains(DcmTS.ExplicitVRLittleEndian))
							_presContextMap[uid].Add(DcmTS.ExplicitVRLittleEndian);
						if (!_presContextMap[uid].Contains(DcmTS.ImplicitVRLittleEndian))
							_presContextMap[uid].Add(DcmTS.ImplicitVRLittleEndian);
					}

					if (SerializedPresentationContexts) {
						foreach (DcmUID uid in _presContextMap.Keys) {
							foreach (DcmTS ts in _presContextMap[uid]) {
								byte pcid = associate.AddPresentationContext(uid);
								associate.AddTransferSyntax(pcid, ts);
							}
						}
					}
					else {
						foreach (DcmUID uid in _presContextMap.Keys) {
							byte pcid = associate.AddOrGetPresentationContext(uid);
							foreach (DcmTS ts in _presContextMap[uid]) {
								associate.AddTransferSyntax(pcid, ts);
							}
						}
					}
				}

				associate.CalledAE = CalledAE;
				associate.CallingAE = CallingAE;
				associate.MaximumPduLength = MaxPduSize;

				SendAssociateRequest(associate);
			}
			else {
				Close();
			}
		}

		protected override void OnConnectionClosed() {
			if (_current != null) {
				_current.Reset();
				AddFile(_current);
				_current = null;
			}

			if (!ClosedOnError && !_cancel) {
				if (PendingCount > 0) {
					Reconnect();
					return;
				}

				if (OnCStoreComplete != null)
					OnCStoreComplete(this);
			}

			if (OnCStoreClosed != null)
				OnCStoreClosed(this);
		}

		protected override void OnReceiveAssociateAccept(DcmAssociate association) {
			SendNextCStoreRequest();
		}

		protected override void OnReceiveReleaseResponse() {
			InternalClose(PendingCount == 0);
		}

		protected override void OnReceiveCStoreResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedInstance, DcmStatus status) {
			_current.Status = status;
			if (OnCStoreResponseReceived != null)
				OnCStoreResponseReceived(this, _current);
			_current = null;
			SendNextCStoreRequest();
		}

		protected override void OnSendDimseProgress(byte pcid, DcmCommand command, DcmDataset dataset, DcmDimseProgress progress) {
			if (OnCStoreRequestProgress != null && _current != null)
				OnCStoreRequestProgress(this, _current, progress);
		}

		private void SendNextCStoreRequest() {
			DateTime linger = DateTime.Now.AddSeconds(Linger + 1);
			while (linger > DateTime.Now && !_cancel) {
				while (_sendQueue.Count > 0 && !_cancel) {
					_current = _sendQueue.Dequeue();
					_sendQueue.Preload(_preloadCount);
					if (_current.Send(this)) {
						_current.Unload();
						return;
					}
					_current.Unload();

					linger = DateTime.Now.AddSeconds(Linger + 1);
				}
				Thread.Sleep(100);
			}
			SendReleaseRequest();
		}
		#endregion

		#region Internal Methods
		internal void Reassociate() {
			SendReleaseRequest();
		}

		internal void SendCStoreRequest(byte pcid, DcmUID instUid, Stream stream) {
			SendCStoreRequest(pcid, NextMessageID(), instUid, Priority, stream);
		}

		internal void SendCStoreRequest(byte pcid, DcmUID instUid, DcmDataset dataset) {
			SendCStoreRequest(pcid, NextMessageID(), instUid, Priority, dataset);
		}
		#endregion
	}
}