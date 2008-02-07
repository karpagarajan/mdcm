// mDCM: A C# DICOM library
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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Dicom.Data;
using Dicom.IO;

using NLog;

namespace Dicom.Network {
	public enum DcmQueryRetrieveLevel {
		Patient,
		Study,
		Series,
		Instance,
		Worklist
	}

	public class DcmDimseProgress {
		public readonly DateTime Started = DateTime.Now;
		public int BytesTransfered { get; internal set; }
		public int EstimatedCommandLength { get; internal set; }
		public int EstimatedDatasetLength { get; internal set; }

		public int EstimatedBytesTotal {
			get { return EstimatedCommandLength + EstimatedDatasetLength; }
		}

		public TimeSpan TimeElapsed {
			get { return DateTime.Now.Subtract(Started); }
		}
	}

	internal class DcmDimseInfo {
		public DcmCommand Command;
		public DcmDataset Dataset;
		public ChunkStream CommandData;
		public ChunkStream DatasetData;
		public DicomStreamReader CommandReader;
		public DicomStreamReader DatasetReader;
		public DcmDimseProgress Progress;
		public string DatasetFile;
		public FileStream DatasetFileStream;
		public Stream DatasetStream;
		public bool IsNewDimse;

		public DcmDimseInfo() {
			Progress = new DcmDimseProgress();
			IsNewDimse = true;
		}

		public void Close() {
			CloseCommand();
			CloseDataset();
		}

		public void CloseCommand() {
			CommandData = null;
			CommandReader = null;
		}

		public void CloseDataset() {
			DatasetStream = null;
			DatasetData = null;
			DatasetReader = null;
			if (DatasetFileStream != null) {
				DatasetFileStream.Dispose();
				DatasetFileStream = null;
			}
		}

		public void Abort() {
			Close();
			if (DatasetFile != null)
				if (File.Exists(DatasetFile))
					File.Delete(DatasetFile);
		}
	}

	public abstract class DcmNetworkBase {
		#region Private Members
		private ushort _messageId;
		private string _host;
		private int _port;
		private DcmSocketType _socketType;
		private int _throttle;
		private Stream _network;
		private DcmSocket _socket;
		private DcmAssociate _assoc;
		private DcmDimseInfo _dimse;
		private Thread _thread;
		private bool _stop;
		private int _dimseTimeout;
		private int _connectTimeout;
		private int _socketTimeout;
		private bool _disableTimeout;
		private bool _isRunning;
		private bool _useFileBuffer;
		private string _logid = "SCU";
		private Logger _log;
		#endregion

		#region Public Constructors
		public DcmNetworkBase() {
			_messageId = 1;
			_connectTimeout = 10;
			_socketTimeout = 30;
			_dimseTimeout = 180;
			_throttle = 0;
			_isRunning = false;
			_useFileBuffer = true;
			_log = Dicom.Debug.Log;
			_logid = "SCx";
		}
		#endregion

		#region Public Properties
		public DcmAssociate Associate {
			get { return _assoc; }
		}

		public int DimseTimeout {
			get { return _dimseTimeout; }
			set { _dimseTimeout = value; }
		}

		public int ConnectTimeout {
			get { return _connectTimeout; }
			set { _connectTimeout = value; }
		}

		public int SocketTimeout {
			get { return _socketTimeout; }
			set {
				_socketTimeout = value;
				if (Socket != null) {
					Socket.SendTimeout = _socketTimeout * 1000;
					Socket.ReceiveTimeout = _socketTimeout * 1000;
				}
			}
		}

		public string Host {
			get { return _host; }
		}

		public int Port {
			get { return _port; }
		}

		public DcmSocketType SocketType {
			get { return _socketType; }
		}

		public int ThrottleSpeed {
			get { return _throttle; }
			set {
				_throttle = value;
				if (Socket != null) {
					Socket.ThrottleSpeed = _throttle;
				}
			}
		}

		public DcmSocket Socket {
			get { return _socket; }
		}

		protected Stream InternalStream {
			get { return _network; }
		}

		public bool IsClosed {
			get { return !_isRunning; }
		}

		public bool UseFileBuffer {
			get { return _useFileBuffer; }
			set { _useFileBuffer = value; }
		}

		public string LogID {
			get { return _logid; }
			set { _logid = value; }
		}

		public Logger Log {
			get { return _log; }
			set { _log = value; }
		}
		#endregion

		#region Public Methods
		public void Connect(string host, int port, DcmSocketType type) {
			_host = host;
			_port = port;
			_socketType = type;
			_stop = false;
			_isRunning = true;
			_thread = new Thread(Connect);
			_thread.IsBackground = true;
			_thread.Start();
		}
		#endregion

		#region Protected Methods
		protected void InitializeNetwork(DcmSocket socket) {
			socket.SendTimeout = _socketTimeout * 1000;
			socket.ReceiveTimeout = _socketTimeout * 1000;
			socket.ThrottleSpeed = _throttle;

			_socket = socket;
			_network = _socket.GetStream();
			_stop = false;
			_isRunning = true;
			_thread = new Thread(Process);
			_thread.IsBackground = true;
			_thread.Start();
		}

		protected void Reconnect() {
			DcmSocketType type = Socket.Type;
			ShutdownNetwork();
			Connect(Host, Port, type);
		}

		protected void ShutdownNetwork() {
			_stop = true;
			if (_thread != null) {
				if (Thread.CurrentThread.ManagedThreadId != _thread.ManagedThreadId)
					_thread.Join();
				_thread = null;	
			}
		}

		protected virtual void OnConnected() {
		}

		protected virtual void OnConnectionClosed() {
		}

		protected virtual void OnNetworkError(Exception e) {
		}

		protected virtual void OnDimseTimeout() {
		}

		protected virtual void OnReceiveAbort(DcmAbortSource source, DcmAbortReason reason) {
			throw new NotImplementedException();
		}

		protected virtual void OnReceiveAssociateRequest(DcmAssociate association) {
			throw new NotImplementedException();
		}

		protected virtual void OnReceiveAssociateAccept(DcmAssociate association) {
			throw new NotImplementedException();
		}

		protected virtual void OnReceiveAssociateReject(DcmRejectResult result, DcmRejectSource source, DcmRejectReason reason) {
			throw new NotImplementedException();
		}

		protected virtual void OnReceiveReleaseRequest() {
			throw new NotImplementedException();
		}

		protected virtual void OnReceiveReleaseResponse() {
			throw new NotImplementedException();
		}



		protected virtual void OnReceiveDimseBegin(byte pcid, DcmCommand command, DcmDataset dataset, DcmDimseProgress progress) {
		}

		protected virtual void OnReceiveDimseProgress(byte pcid, DcmCommand command, DcmDataset dataset, DcmDimseProgress progress) {
		}

		protected virtual void OnReceiveDimse(byte pcid, DcmCommand command, DcmDataset dataset, DcmDimseProgress progress) {
		}

		protected virtual void OnSendDimseBegin(byte pcid, DcmCommand command, DcmDataset dataset, DcmDimseProgress progress) {
		}

		protected virtual void OnSendDimseProgress(byte pcid, DcmCommand command, DcmDataset dataset, DcmDimseProgress progress) {
		}

		protected virtual void OnSendDimse(byte pcid, DcmCommand command, DcmDataset dataset, DcmDimseProgress progress) {
		}



		protected virtual void OnPreReceiveCStoreRequest(byte presentationID, ushort messageID, DcmUID affectedInstance,
			DcmPriority priority, string moveAE, ushort moveMessageID, out string fileName) {
			if (UseFileBuffer) {
				fileName = Path.GetTempFileName();
			} else {
				fileName = null;
			}
		}

		protected virtual void OnReceiveCStoreRequest(byte presentationID, ushort messageID, DcmUID affectedInstance, 
			DcmPriority priority, string moveAE, ushort moveMessageID, DcmDataset dataset, string fileName) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnPostReceiveCStoreRequest(byte presentationID, ushort messageID, DcmUID affectedInstance, 
			DcmDataset dataset, string fileName) {
			if (fileName != null)
				if (File.Exists(fileName))
					File.Delete(fileName);
		}

		protected virtual void OnReceiveCStoreResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedInstance, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCEchoRequest(byte presentationID, ushort messageID, DcmPriority priority) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCEchoResponse(byte presentationID, ushort messageIdRespondedTo, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCFindRequest(byte presentationID, ushort messageID, DcmPriority priority, DcmDataset dataset) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCFindResponse(byte presentationID, ushort messageIdRespondedTo, DcmDataset dataset, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCGetRequest(byte presentationID, ushort messageID, DcmPriority priority, DcmDataset dataset) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCGetResponse(byte presentationID, ushort messageIdRespondedTo, DcmDataset dataset, 
			DcmStatus status, ushort remain, ushort complete, ushort warning, ushort failure) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCMoveRequest(byte presentationID, ushort messageID, string destinationAE, DcmPriority priority, DcmDataset dataset) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCMoveResponse(byte presentationID, ushort messageIdRespondedTo, DcmDataset dataset, 
			DcmStatus status, ushort remain, ushort complete, ushort warning, ushort failure) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveCCancelRequest(byte presentationID, ushort messageIdRespondedTo) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNEventReportRequest(byte presentationID, ushort messageID, DcmUID affectedClass, DcmUID affectedInstance,
			ushort eventTypeID, DcmDataset dataset) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNEventReportResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			ushort eventTypeID, DcmDataset dataset, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNGetRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance, DcmTag[] attributes) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNGetResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			DcmDataset dataset, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNSetRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance, DcmDataset dataset) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNSetResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			DcmDataset dataset, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNActionRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance,
			ushort actionTypeID, DcmDataset dataset) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNActionResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			ushort actionTypeID, DcmDataset dataset, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNCreateRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance, DcmDataset dataset) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNCreateResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			DcmDataset dataset, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNDeleteRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}

		protected virtual void OnReceiveNDeleteResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance, DcmStatus status) {
			SendAbort(DcmAbortSource.ServiceProvider, DcmAbortReason.NotSpecified);
		}


		protected ushort NextMessageID() {
			return _messageId++;
		}

		protected void SendAssociateRequest(DcmAssociate associate) {
			_assoc = associate;
			Log.Info("{0} -> Association request:\n{1}", LogID, Associate.ToString());
			AAssociateRQ pdu = new AAssociateRQ(_assoc);
			SendRawPDU(pdu.Write());
		}

		protected void SendAssociateAccept(DcmAssociate associate) {
			Log.Info("{0} -> Association accept:\n{1}", LogID, Associate.ToString());
			AAssociateAC pdu = new AAssociateAC(_assoc);
			SendRawPDU(pdu.Write());
		}

		protected void SendAssociateReject(DcmRejectResult result, DcmRejectSource source, DcmRejectReason reason) {
			Log.Info("{0} -> Association reject [result: {1}; source: {2}; reason: {3}]", LogID, result, source, reason);
			AAssociateRJ pdu = new AAssociateRJ(result, source, reason);
			SendRawPDU(pdu.Write());
		}

		protected void SendReleaseRequest() {
			Log.Info("{0} -> Association release request", LogID);
			AReleaseRQ pdu = new AReleaseRQ();
			SendRawPDU(pdu.Write());
		}

		protected void SendReleaseResponse() {
			Log.Info("{0} -> Association release response", LogID);
			AReleaseRP pdu = new AReleaseRP();
			SendRawPDU(pdu.Write());
		}

		protected void SendAbort(DcmAbortSource source, DcmAbortReason reason) {
			Log.Info("{0} -> Abort [source: {1}; reason: {2}]", LogID, source, reason);
			AAbort pdu = new AAbort(source, reason);
			SendRawPDU(pdu.Write());
		}

		protected void SendCEchoRequest(byte presentationID, ushort messageID, DcmPriority priority) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateRequest(messageID, DcmCommandField.CEchoRequest, affectedClass, priority, false);
			Log.Info("{0} -> C-Echo request [pc: {1}; id: {2}]", LogID, presentationID, messageID);
			SendDimse(presentationID, command, null);
		}

		protected void SendCEchoResponse(byte presentationID, ushort messageIdRespondedTo, DcmStatus status) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateResponse(messageIdRespondedTo, DcmCommandField.CEchoResponse, affectedClass, status, false);
			Log.Info("{0} -> C-Echo response [id: {1}]", LogID, messageIdRespondedTo);
			SendDimse(presentationID, command, null);
		}

		protected void SendCStoreRequest(byte presentationID, ushort messageID, DcmUID affectedInstance,
			DcmPriority priority, DcmDataset dataset) {
			SendCStoreRequest(presentationID, messageID, affectedInstance, priority, null, 0, dataset);
		}

		protected void SendCStoreRequest(byte presentationID, ushort messageID, DcmUID affectedInstance, 
			DcmPriority priority, string moveAE, ushort moveMessageID, DcmDataset dataset) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);

			DcmCommand command = CreateRequest(messageID, DcmCommandField.CStoreRequest, affectedClass, priority, true);
			command.AffectedSOPInstanceUID = affectedInstance;
			if (moveAE != null && moveAE != String.Empty) {
				command.MoveOriginator = moveAE;
				command.MoveOriginatorMessageID = moveMessageID;
			}

			Log.Info("{0} -> C-Store request [pc: {1}; id: {2}]", LogID, presentationID, messageID);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendCStoreRequest(byte presentationID, ushort messageID, DcmUID affectedInstance,
			DcmPriority priority, Stream datastream) {
			SendCStoreRequest(presentationID, messageID, affectedInstance, priority, null, 0, datastream);
		}

		protected void SendCStoreRequest(byte presentationID, ushort messageID, DcmUID affectedInstance,
			DcmPriority priority, string moveAE, ushort moveMessageID, Stream datastream) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);

			DcmCommand command = CreateRequest(messageID, DcmCommandField.CStoreRequest, affectedClass, priority, true);
			command.AffectedSOPInstanceUID = affectedInstance;
			if (moveAE != null && moveAE != String.Empty) {
				command.MoveOriginator = moveAE;
				command.MoveOriginatorMessageID = moveMessageID;
			}

			Log.Info("{0} -> C-Store request [pc: {1}; id: {2}] (stream)", LogID, presentationID, messageID);
			SendDimseStream(presentationID, command, datastream);
		}

		protected void SendCStoreResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedInstance, DcmStatus status) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateResponse(messageIdRespondedTo, DcmCommandField.CStoreResponse, affectedClass, status, false);
			command.AffectedSOPInstanceUID = affectedInstance;
			Log.Info("{0} -> C-Store response [id: {1}]: {2}", LogID, messageIdRespondedTo, status);
			SendDimse(presentationID, command, null);
		}

		protected void SendCFindRequest(byte presentationID, ushort messageID, DcmPriority priority, DcmDataset dataset) {
			String level = dataset.GetString(DcmTags.QueryRetrieveLevel, "UNKNOWN");
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateRequest(messageID, DcmCommandField.CFindRequest, affectedClass, priority, true);
			Log.Info("{0} -> C-Find request [pc: {1}; id: {2}; lvl: {3}]", LogID, presentationID, messageID, level);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendCFindResponse(byte presentationID, ushort messageIdRespondedTo, DcmStatus status) {
			SendCFindResponse(presentationID, messageIdRespondedTo, null, status);
		}

		protected void SendCFindResponse(byte presentationID, ushort messageIdRespondedTo, DcmDataset dataset, DcmStatus status) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateResponse(messageIdRespondedTo, DcmCommandField.CFindResponse, affectedClass, status, dataset != null);
			Log.Info("{0} -> C-Find response [id: {1}]: {2}", LogID, messageIdRespondedTo, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendCGetRequest(byte presentationID, ushort messageID, DcmPriority priority, DcmDataset dataset) {
			String level = dataset.GetString(DcmTags.QueryRetrieveLevel, "UNKNOWN");
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateRequest(messageID, DcmCommandField.CGetRequest, affectedClass, priority, true);
			Log.Info("{0} -> C-Get request [pc: {1}; id: {2}; lvl: {3}]", LogID, presentationID, messageID, level);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendCGetResponse(byte presentationID, ushort messageIdRespondedTo, DcmStatus status,
			ushort remain, ushort complete, ushort warning, ushort failure) {
			SendCGetResponse(presentationID, messageIdRespondedTo, null, status, remain, complete, warning, failure);
		}

		protected void SendCGetResponse(byte presentationID, ushort messageIdRespondedTo, DcmDataset dataset, 
			DcmStatus status, ushort remain, ushort complete, ushort warning, ushort failure) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateResponse(messageIdRespondedTo, DcmCommandField.CGetResponse, affectedClass, status, dataset != null);
			command.RemainingSuboperations = remain;
			command.CompletedSuboperations = complete;
			command.WarningSuboperations = warning;
			command.FailedSuboperations = failure;
			Log.Info("{0} -> C-Get response [id: {1}; remain: {2}; complete: {3}; warning: {4}; failure: {5}]: {6}", 
				LogID, messageIdRespondedTo, remain, complete, warning, failure, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendCMoveRequest(byte presentationID, ushort messageID, string destinationAE, DcmPriority priority, DcmDataset dataset) {
			String level = dataset.GetString(DcmTags.QueryRetrieveLevel, "UNKNOWN");
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateRequest(messageID, DcmCommandField.CMoveRequest, affectedClass, priority, true);
			command.MoveDestination = destinationAE;
			Log.Info("{0} -> C-Move request [pc: {1}; id: {2}; lvl: {3}]", LogID, presentationID, messageID, level);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendCMoveResponse(byte presentationID, ushort messageIdRespondedTo, DcmStatus status,
			ushort remain, ushort complete, ushort warning, ushort failure) {
			SendCMoveResponse(presentationID, messageIdRespondedTo, null, status, remain, complete, warning, failure);
		}

		protected void SendCMoveResponse(byte presentationID, ushort messageIdRespondedTo, DcmDataset dataset, DcmStatus status,
			ushort remain, ushort complete, ushort warning, ushort failure) {
			DcmUID affectedClass = Associate.GetAbstractSyntax(presentationID);
			DcmCommand command = CreateResponse(messageIdRespondedTo, DcmCommandField.CMoveResponse, affectedClass, status, dataset != null);
			command.RemainingSuboperations = remain;
			command.CompletedSuboperations = complete;
			command.WarningSuboperations = warning;
			command.FailedSuboperations = failure;
			Log.Info("{0} -> C-Move response [id: {1}; remain: {2}; complete: {3}; warning: {4}; failure: {5}]: {6}",
				LogID, messageIdRespondedTo, remain, complete, warning, failure, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendCCancelRequest(byte presentationID, ushort messageIdRespondedTo) {
			DcmCommand command = new DcmCommand();
			command.CommandField = DcmCommandField.CCancelRequest;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = false;
			Log.Info("{0} -> C-Cancel request [pc: {1}; id: {2}]", LogID, presentationID, messageIdRespondedTo);
			SendDimse(presentationID, command, null);
		}

		protected void SendNEventReportRequest(byte presentationID, ushort messageID, DcmUID affectedClass, DcmUID affectedInstance, 
			ushort eventTypeID, DcmDataset dataset) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = DcmCommandField.NEventReportRequest;
			command.MessageID = messageID;
			command.HasDataset = (dataset != null);
			command.AffectedSOPInstanceUID = affectedInstance;
			command.EventTypeID = eventTypeID;
			Log.Info("{0} -> N-EventReport request [pc: {1}; id: {2}; class: {3}; event: {4:x4}]", 
				LogID, presentationID, messageID, affectedClass, eventTypeID);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNEventReportResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			ushort eventTypeID, DcmDataset dataset, DcmStatus status) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = DcmCommandField.NEventReportResponse;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = (dataset != null);
			command.Status = status;
			command.AffectedSOPInstanceUID = affectedInstance;
			command.EventTypeID = eventTypeID;
			Log.Info("{0} -> N-EventReport response [id: {1}; class: {2}; event: {3:x4}]: {4}}",
				LogID, messageIdRespondedTo, affectedClass, eventTypeID, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNGetRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance, DcmTag[] attributes) {
			DcmCommand command = new DcmCommand();
			command.RequestedSOPClassUID = requestedClass;
			command.CommandField = DcmCommandField.NGetRequest;
			command.MessageID = messageID;
			command.HasDataset = false;
			command.RequestedSOPInstanceUID = requestedInstance;
			command.AttributeIdentifierList = new DcmAttributeTag(DcmTags.AttributeIdentifierList);
			command.AttributeIdentifierList.SetValues(attributes);
			Log.Info("{0} -> N-Get request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
			SendDimse(presentationID, command, null);
		}

		protected void SendNGetResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance, 
			DcmDataset dataset, DcmStatus status) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = DcmCommandField.NGetResponse;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = (dataset != null);
			command.Status = status;
			command.AffectedSOPInstanceUID = affectedInstance;
			Log.Info("{0} -> N-Get response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNSetRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance, DcmDataset dataset) {
			DcmCommand command = new DcmCommand();
			command.RequestedSOPClassUID = requestedClass;
			command.CommandField = DcmCommandField.NSetRequest;
			command.MessageID = messageID;
			command.HasDataset = (dataset != null);
			command.RequestedSOPInstanceUID = requestedInstance;
			Log.Info("{0} -> N-Set request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNSetResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			DcmDataset dataset, DcmStatus status) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = DcmCommandField.NSetResponse;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = (dataset != null);
			command.Status = status;
			command.AffectedSOPInstanceUID = affectedInstance;
			Log.Info("{0} -> N-Set response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNActionRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance, 
			ushort actionTypeID, DcmDataset dataset) {
			DcmCommand command = new DcmCommand();
			command.RequestedSOPClassUID = requestedClass;
			command.CommandField = DcmCommandField.NActionRequest;
			command.MessageID = messageID;
			command.HasDataset = (dataset != null);
			command.RequestedSOPInstanceUID = requestedInstance;
			command.ActionTypeID = actionTypeID;
			Log.Info("{0} -> N-Action request [pc: {1}; id: {2}; class: {3}; action: {4:x4}]", 
				LogID, presentationID, messageID, requestedClass, actionTypeID);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNActionResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance, 
			ushort actionTypeID, DcmDataset dataset, DcmStatus status) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = DcmCommandField.NActionResponse;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = (dataset != null);
			command.Status = status;
			command.AffectedSOPInstanceUID = affectedInstance;
			command.ActionTypeID = actionTypeID;
			Log.Info("{0} -> N-Action response [id: {1}; class: {2}; action: {3:x4}]: {4}",
				LogID, messageIdRespondedTo, affectedClass, actionTypeID, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNCreateRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance, DcmDataset dataset) {
			DcmCommand command = new DcmCommand();
			command.RequestedSOPClassUID = requestedClass;
			command.CommandField = DcmCommandField.NCreateRequest;
			command.MessageID = messageID;
			command.HasDataset = (dataset != null);
			command.RequestedSOPInstanceUID = requestedInstance;
			Log.Info("{0} -> N-Create request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNCreateResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance,
			DcmDataset dataset, DcmStatus status) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = DcmCommandField.NCreateResponse;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = (dataset != null);
			command.Status = status;
			command.AffectedSOPInstanceUID = affectedInstance;
			Log.Info("{0} -> N-Create response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
			SendDimse(presentationID, command, dataset);
		}

		protected void SendNDeleteRequest(byte presentationID, ushort messageID, DcmUID requestedClass, DcmUID requestedInstance) {
			DcmCommand command = new DcmCommand();
			command.RequestedSOPClassUID = requestedClass;
			command.CommandField = DcmCommandField.NDeleteRequest;
			command.MessageID = messageID;
			command.HasDataset = false;
			command.RequestedSOPInstanceUID = requestedInstance;
			Log.Info("{0} -> N-Delete request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
			SendDimse(presentationID, command, null);
		}

		protected void SendNDeleteResponse(byte presentationID, ushort messageIdRespondedTo, DcmUID affectedClass, DcmUID affectedInstance, DcmStatus status) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = DcmCommandField.NDeleteResponse;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = false;
			command.Status = status;
			command.AffectedSOPInstanceUID = affectedInstance;
			Log.Info("{0} -> N-Delete response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
			SendDimse(presentationID, command, null);
		}
		#endregion

		#region Private Methods
		private DcmCommand CreateRequest(ushort messageID, DcmCommandField commandField, DcmUID affectedClass, DcmPriority priority, bool hasDataset) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = commandField;
			command.MessageID = messageID;
			command.Priority = priority;
			command.HasDataset = hasDataset;
			return command;
		}

		private DcmCommand CreateResponse(ushort messageIdRespondedTo, DcmCommandField commandField, DcmUID affectedClass, DcmStatus status, bool hasDataset) {
			DcmCommand command = new DcmCommand();
			command.AffectedSOPClassUID = affectedClass;
			command.CommandField = commandField;
			command.MessageIDRespondedTo = messageIdRespondedTo;
			command.HasDataset = hasDataset;
			command.Status = status;
			return command;
		}

		private void Connect() {
			bool success = false;

			try {
				_socket = DcmSocket.Create(_socketType);

				Log.Info("{0} -> Connecting to server at {1}:{2}", LogID, _host, _port);

				if (_connectTimeout == 0) {
					_socket.Connect(_host, _port);
				} else {
					try {
						_socket.Blocking = false;
						_socket.Connect(_host, _port);
					}
					catch (SocketException e) {
						if (e.SocketErrorCode != SocketError.WouldBlock)
							throw;
					}

					if (!_socket.Poll(_connectTimeout * 1000000, SelectMode.SelectWrite))
						throw new SocketException((int)SocketError.TimedOut);
					
					_socket.Blocking = true;
				}

				if (_socketType == DcmSocketType.TLS)
					Log.Info("{0} -> Authenticating SSL/TLS for server: {1}", LogID, _socket.RemoteEndPoint);

				
				_socket.SendTimeout = _socketTimeout * 1000;
				_socket.ReceiveTimeout = _socketTimeout * 1000;
				_socket.ThrottleSpeed = _throttle;
				_network = _socket.GetStream();
				success = true;
			}
			catch (SocketException e) {
				if (e.SocketErrorCode == SocketError.TimedOut)
					Log.Error("{0} -> Network timeout after {1} seconds", LogID, SocketTimeout);
				else
					Log.Error("{0} -> Network error: {1}", LogID, e.Message);
				OnNetworkError(e);
				OnConnectionClosed();
			}
			catch (Exception e) {
				Log.Error("{0} -> Processing failure: {1}", LogID, e.Message);
				OnNetworkError(e);
				OnConnectionClosed();
			}
			finally {
				if (!success) {
					try { _network.Close(); }
					catch { }
					_network = null;
					try { _socket.Close(); }
					catch { }
					_socket = null;
					_isRunning = false;
				}
			}

			if (success)
				Process();
		}

		private void Process() {
			try {
				OnConnected();
				_disableTimeout = false;
				DateTime timeout = DateTime.Now.AddSeconds(DimseTimeout);
				while (!_stop) {
					if (_socket.Poll(500, SelectMode.SelectRead)) {
						if (_socket.Available == 0) {
							// connection closed
							break;
						}
						ProcessNextPDU();
						timeout = DateTime.Now.AddSeconds(DimseTimeout);
					} else if (_disableTimeout) {
						timeout = DateTime.Now.AddSeconds(DimseTimeout);
					} else if (DateTime.Now > timeout) {
						Log.Error("{0} -> DIMSE timeout after {1} seconds", LogID, DimseTimeout);
						OnDimseTimeout();
						_stop = true;
					} else {
						Thread.Sleep(100);
					}
				}
				Log.Info("{0} -> Connection closed", LogID);
				OnConnectionClosed();
			}
			catch (SocketException e) {
				if (e.SocketErrorCode == SocketError.TimedOut)
					Log.Error("{0} -> Network timeout after {1} seconds", LogID, SocketTimeout);
				else
					Log.Error("{0} -> Network error: {2}", LogID, e.Message);
				OnNetworkError(e);
				OnConnectionClosed();
			}
			catch (Exception e) {
				Log.Error("{0} -> Processing failure: {1}", LogID, e.Message);
				OnNetworkError(e);
				Log.Info("{0} -> Connection closed", LogID);
				OnConnectionClosed();
			}
			finally {
				try { _network.Close(); } catch { }
				_network = null;
				try { _socket.Close(); } catch { }
				_socket = null;
				_isRunning = false;
			}
		}

		private bool ProcessNextPDU() {
			RawPDU raw = new RawPDU(_network);

			if (raw.Type == 0x04) {
				if (_dimse == null) {
					_dimse = new DcmDimseInfo();
				}
			}

			try {
				raw.ReadPDU();

				switch (raw.Type) {
				case 0x01: {
						_assoc = new DcmAssociate();
						AAssociateRQ pdu = new AAssociateRQ(_assoc);
						pdu.Read(raw);
						Log.Info("{0} <- Association request:\n{1}", LogID, Associate.ToString());
						OnReceiveAssociateRequest(_assoc);
						return true;
					}
				case 0x02: {
						AAssociateAC pdu = new AAssociateAC(_assoc);
						pdu.Read(raw);
						Log.Info("{0} <- Association accept:\n{1}", LogID, Associate.ToString());
						OnReceiveAssociateAccept(_assoc);
						return true;
					}
				case 0x03: {
						AAssociateRJ pdu = new AAssociateRJ();
						pdu.Read(raw);
						Log.Info("{0} <- Association reject [result: {1}; source: {2}; reason: {3}]", LogID, pdu.Result, pdu.Source, pdu.Reason);
						OnReceiveAssociateReject(pdu.Result, pdu.Source, pdu.Reason);
						return true;
					}
				case 0x04: {
						PDataTF pdu = new PDataTF();
						pdu.Read(raw);
						//Log.Debug("{0} <- P-Data-TF", LogID);
						return ProcessPDataTF(pdu);
					}
				case 0x05: {
						AReleaseRQ pdu = new AReleaseRQ();
						pdu.Read(raw);
						Log.Info("{0} <- Association release request", LogID);
						OnReceiveReleaseRequest();
						return true;
					}
				case 0x06: {
						AReleaseRP pdu = new AReleaseRP();
						pdu.Read(raw);
						Log.Info("{0} <- Association release response", LogID);
						OnReceiveReleaseResponse();
						return true;
					}
				case 0x07: {
						AAbort pdu = new AAbort();
						pdu.Read(raw);
						Log.Info("{0} <- Association abort: {1} - {2}", LogID, pdu.Source, pdu.Reason);
						OnReceiveAbort(pdu.Source, pdu.Reason);
						return true;
					}
				case 0xFF: {
						return false;
					}
				default:
					throw new DcmNetworkException("Unknown PDU type");
				}
			} catch (Exception e) {
				Log.Error("{0} -> Error reading PDU [type: 0x{1:x2}]: {2}", LogID, raw.Type, e.Message);
				OnNetworkError(e);
				//String file = String.Format(@"{0}\Errors\{1}.pdu",
				//    Dicom.Debug.GetStartDirectory(), DateTime.Now.Ticks);
				//Directory.CreateDirectory(Dicom.Debug.GetStartDirectory() + @"\Errors");
				//raw.Save(file);
				return false;
			}
		}

		private bool ProcessPDataTF(PDataTF pdu) {
			try {
				byte pcid = 0;
				foreach (PDV pdv in pdu.PDVs) {
					pcid = pdv.PCID;
					if (pdv.IsCommand) {
						if (_dimse.CommandData == null)
							_dimse.CommandData = new ChunkStream();

						_dimse.CommandData.AddChunk(pdv.Value);

						if (_dimse.Command == null) {
							_dimse.Command = new DcmCommand();
						}

						if (_dimse.CommandReader == null) {
							_dimse.CommandReader = new DicomStreamReader(_dimse.CommandData);
							_dimse.CommandReader.Dataset = _dimse.Command;
						}

						_dimse.CommandReader.Read(null, DicomReadOptions.Default);

						_dimse.Progress.BytesTransfered += pdv.Value.Length;
						_dimse.Progress.EstimatedCommandLength = (int)_dimse.CommandReader.BytesEstimated;

						if (pdv.IsLastFragment) {
							_dimse.CloseCommand();

							bool isLast = true;
							if (_dimse.Command.Contains(DcmTags.DataSetType)) {
								if (_dimse.Command.GetUInt16(DcmTags.DataSetType, 0x0101) != 0x0101) {
									isLast = false;

									DcmCommandField commandField = (DcmCommandField)_dimse.Command.GetUInt16(DcmTags.CommandField, 0);
									if (commandField == DcmCommandField.CStoreRequest) {
										ushort messageID = _dimse.Command.GetUInt16(DcmTags.MessageID, 1);
										DcmPriority priority = (DcmPriority)_dimse.Command.GetUInt16(DcmTags.Priority, 0);
										DcmUID affectedInstance = _dimse.Command.GetUID(DcmTags.AffectedSOPInstanceUID);
										string moveAE = _dimse.Command.GetString(DcmTags.MoveOriginator, null);
										ushort moveMessageID = _dimse.Command.GetUInt16(DcmTags.MoveOriginatorMessageID, 1);
										OnPreReceiveCStoreRequest(pcid, messageID, affectedInstance, priority, 
											moveAE, moveMessageID, out _dimse.DatasetFile);

										if (_dimse.DatasetFile != null) {
											DcmPresContext pres = Associate.GetPresentationContext(pcid);

											DicomFileFormat ff = new DicomFileFormat();
											ff.FileMetaInfo.FileMetaInformationVersion = DcmFileMetaInfo.Version;
											ff.FileMetaInfo.MediaStorageSOPClassUID = pres.AbstractSyntax;
											ff.FileMetaInfo.MediaStorageSOPInstanceUID = affectedInstance;
											ff.FileMetaInfo.TransferSyntax = pres.AcceptedTransferSyntax;
											ff.FileMetaInfo.ImplementationClassUID = Implementation.ClassUID;
											ff.FileMetaInfo.ImplementationVersionName = Implementation.Version;
											ff.FileMetaInfo.SourceApplicationEntityTitle = Associate.CalledAE;
											ff.Save(_dimse.DatasetFile, DicomWriteOptions.Default);

											_dimse.DatasetFileStream = new FileStream(_dimse.DatasetFile, FileMode.Open);
											_dimse.DatasetFileStream.Seek(0, SeekOrigin.End);
											_dimse.DatasetStream = _dimse.DatasetFileStream;
										}
									}
								}
							}
							if (isLast) {
								if (_dimse.IsNewDimse)
									OnReceiveDimseBegin(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
								OnReceiveDimseProgress(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
								OnReceiveDimse(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
								ProcessDimse(pcid);
								_dimse = null;
								return true;
							}
						}
					} else {
						if (_dimse.DatasetFile != null) {
							long pos = _dimse.DatasetFileStream.Position;
							_dimse.DatasetFileStream.Seek(0, SeekOrigin.End);
							_dimse.DatasetFileStream.Write(pdv.Value, 0, pdv.Value.Length);
							_dimse.DatasetFileStream.Position = pos;
						} else {
							if (_dimse.DatasetData == null) {
								_dimse.DatasetData = new ChunkStream();
								_dimse.DatasetStream = _dimse.DatasetData;
							}
							_dimse.DatasetData.AddChunk(pdv.Value);
						}

						if (_dimse.Dataset == null) {
							DcmTS ts = _assoc.GetAcceptedTransferSyntax(pdv.PCID);
							_dimse.Dataset = new DcmDataset(ts);
						}

						if (_dimse.DatasetReader == null) {
							_dimse.DatasetReader = new DicomStreamReader(_dimse.DatasetStream);
							_dimse.DatasetReader.Dataset = _dimse.Dataset;
						}
						
						_dimse.Progress.BytesTransfered += pdv.Value.Length;

						long remaining = _dimse.DatasetReader.BytesRemaining + pdv.Value.Length;
						if (remaining >= _dimse.DatasetReader.BytesNeeded || pdv.IsLastFragment) {
							_dimse.DatasetReader.Read(null, DicomReadOptions.Default |
															DicomReadOptions.DeferLoadingLargeElements |
															DicomReadOptions.DeferLoadingPixelData);

							_dimse.Progress.EstimatedDatasetLength = (int)_dimse.DatasetReader.BytesEstimated;
						}

						if (pdv.IsLastFragment) {
							_dimse.Close();

							if (_dimse.IsNewDimse)
								OnReceiveDimseBegin(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
							OnReceiveDimseProgress(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
							OnReceiveDimse(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
							ProcessDimse(pcid);
							_dimse = null;
							return true;
						}
					}
				}

				if (_dimse.IsNewDimse) {
					OnReceiveDimseBegin(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
					_dimse.IsNewDimse = false;
				} else {
					OnReceiveDimseProgress(pcid, _dimse.Command, _dimse.Dataset, _dimse.Progress);
				}

				return true;
			} catch (Exception e) {
				Log.Error("{0} -> Error reading DIMSE: {1}", e.Message);
				_dimse.Abort();
				_dimse = null;
				return false;
			}
		}

		private bool ProcessDimse(byte presentationID) {
			DcmCommandField commandField = _dimse.Command.CommandField;

			if (commandField == DcmCommandField.CStoreRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmPriority priority = _dimse.Command.Priority;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				string moveAE = _dimse.Command.MoveOriginator;
				ushort moveMessageID = _dimse.Command.MoveOriginatorMessageID;
				Log.Info("{0} <- C-Store request [pc: {1}; id: {2}]{3}", 
					LogID, presentationID, messageID, (_dimse.DatasetFile != null ? " (stream)" : ""));
				try {
					OnReceiveCStoreRequest(presentationID, messageID, affectedInstance, priority, moveAE, moveMessageID, _dimse.Dataset, _dimse.DatasetFile);
				} finally {
					OnPostReceiveCStoreRequest(presentationID, messageID, affectedInstance, _dimse.Dataset, _dimse.DatasetFile);
				}
				return true;
			}

			if (commandField == DcmCommandField.CStoreResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- C-Store response [id: {1}]: {2}", LogID, messageIdRespondedTo, status);
				OnReceiveCStoreResponse(presentationID, messageIdRespondedTo, affectedInstance, status);
				return true;
			}

			if (commandField == DcmCommandField.CEchoRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmPriority priority = _dimse.Command.Priority;
				Log.Info("{0} <- C-Echo request [pc: {1}; id: {2}]", LogID, presentationID, messageID);
				OnReceiveCEchoRequest(presentationID, messageID, priority);
				return true;
			}

			if (commandField == DcmCommandField.CEchoResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- C-Echo response [{1}]: {2}", LogID, messageIdRespondedTo, status);
				OnReceiveCEchoResponse(presentationID, messageIdRespondedTo, status);
				return true;
			}

			if (commandField == DcmCommandField.CFindRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmPriority priority = _dimse.Command.Priority;
				String level = _dimse.Dataset.GetString(DcmTags.QueryRetrieveLevel, "UNKNOWN");
				Log.Info("{0} <- C-Find request [pc: {1}; id: {2}; lvl: {3}]", LogID, presentationID, messageID, level);
				OnReceiveCFindRequest(presentationID, messageID, priority, _dimse.Dataset);
				return true;
			}

			if (commandField == DcmCommandField.CFindResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- C-Find response [id: {1}]: {2}", LogID, messageIdRespondedTo, status);
				OnReceiveCFindResponse(presentationID, messageIdRespondedTo, _dimse.Dataset, status);
				return true;
			}

			if (commandField == DcmCommandField.CGetRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmPriority priority = _dimse.Command.Priority;
				String level = _dimse.Dataset.GetString(DcmTags.QueryRetrieveLevel, "UNKNOWN");
				Log.Info("{0} <- C-Get request [pc: {1}; id: {2}; lvl: {3}]", LogID, presentationID, messageID, level);
				OnReceiveCGetRequest(presentationID, messageID, priority, _dimse.Dataset);
				return true;
			}

			if (commandField == DcmCommandField.CGetResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmStatus status = _dimse.Command.Status;
				ushort remain = _dimse.Command.RemainingSuboperations;
				ushort complete = _dimse.Command.CompletedSuboperations;
				ushort warning = _dimse.Command.WarningSuboperations;
				ushort failure = _dimse.Command.FailedSuboperations;
				Log.Info("{0} <- C-Get response [id: {1}; remain: {2}; complete: {3}; warning: {4}; failure: {5}]: {6}",
					LogID, messageIdRespondedTo, remain, complete, warning, failure, status);
				OnReceiveCGetResponse(presentationID, messageIdRespondedTo, _dimse.Dataset, status, remain, complete, warning, failure);
				return true;
			}

			if (commandField == DcmCommandField.CMoveRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmPriority priority = _dimse.Command.Priority;
				string destAE = _dimse.Command.MoveDestination;
				String level = _dimse.Dataset.GetString(DcmTags.QueryRetrieveLevel, "UNKNOWN");
				Log.Info("{0} <- C-Move request [pc: {1}; id: {2}; lvl: {3}]", LogID, presentationID, messageID, level);
				OnReceiveCMoveRequest(presentationID, messageID, destAE, priority, _dimse.Dataset);
				return true;
			}

			if (commandField == DcmCommandField.CMoveResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmStatus status = _dimse.Command.Status;
				ushort remain = _dimse.Command.RemainingSuboperations;
				ushort complete = _dimse.Command.CompletedSuboperations;
				ushort warning = _dimse.Command.WarningSuboperations;
				ushort failure = _dimse.Command.FailedSuboperations;
				Log.Info("{0} <- C-Move response [id: {1}; remain: {2}; complete: {3}; warning: {4}; failure: {5}]: {6}",
					LogID, messageIdRespondedTo, remain, complete, warning, failure, status);
				OnReceiveCMoveResponse(presentationID, messageIdRespondedTo, _dimse.Dataset, status, remain, complete, warning, failure);
				return true;
			}

			if (commandField == DcmCommandField.CCancelRequest) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				Log.Info("{0} <- C-Cancel request [pc: {1}; id: {2}]", LogID, presentationID, messageIdRespondedTo);
				OnReceiveCCancelRequest(presentationID, messageIdRespondedTo);
				return true;
			}

			if (commandField == DcmCommandField.NEventReportRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmUID affectedClass = _dimse.Command.AffectedSOPClassUID;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				ushort eventTypeID = _dimse.Command.EventTypeID;
				Log.Info("{0} <- N-EventReport request [pc: {1}; id: {2}; class: {3}; event: {4:x4}]", 
					LogID, presentationID, messageID, affectedClass, eventTypeID);
				OnReceiveNEventReportRequest(presentationID, messageID, affectedClass, affectedInstance, eventTypeID, _dimse.Dataset);
				return true;
			}

			if (commandField == DcmCommandField.NEventReportResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmUID affectedClass = _dimse.Command.AffectedSOPClassUID;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				ushort eventTypeID = _dimse.Command.EventTypeID;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- N-EventReport response [id: {1}; class: {2}; event: {3:x4}]: {4}}",
					LogID, messageIdRespondedTo, affectedClass, eventTypeID, status);
				OnReceiveNEventReportResponse(presentationID, messageIdRespondedTo, affectedClass, affectedInstance, eventTypeID, _dimse.Dataset, status);
				return true;
			}

			if (commandField == DcmCommandField.NGetRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmUID requestedClass = _dimse.Command.RequestedSOPClassUID;
				DcmUID requestedInstance = _dimse.Command.RequestedSOPInstanceUID;
				DcmTag[] attributes = new DcmTag[0];
				if (_dimse.Command.AttributeIdentifierList != null)
					attributes = _dimse.Command.AttributeIdentifierList.GetValues();
				Log.Info("{0} <- N-Get request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
				OnReceiveNGetRequest(presentationID, messageID, requestedClass, requestedInstance, attributes);
				return true;
			}

			if (commandField == DcmCommandField.NGetResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmUID affectedClass = _dimse.Command.AffectedSOPClassUID;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- N-Get response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
				OnReceiveNGetResponse(presentationID, messageIdRespondedTo, affectedClass, affectedInstance, _dimse.Dataset, status);
				return true;
			}

			if (commandField == DcmCommandField.NSetRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmUID requestedClass = _dimse.Command.RequestedSOPClassUID;
				DcmUID requestedInstance = _dimse.Command.RequestedSOPInstanceUID;
				Log.Info("{0} <- N-Set request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
				OnReceiveNSetRequest(presentationID, messageID, requestedClass, requestedInstance, _dimse.Dataset);
				return true;
			}

			if (commandField == DcmCommandField.NSetResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmUID affectedClass = _dimse.Command.AffectedSOPClassUID;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- N-Set response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
				OnReceiveNSetResponse(presentationID, messageIdRespondedTo, affectedClass, affectedInstance, _dimse.Dataset, status);
				return true;
			}

			if (commandField == DcmCommandField.NActionRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmUID requestedClass = _dimse.Command.RequestedSOPClassUID;
				DcmUID requestedInstance = _dimse.Command.RequestedSOPInstanceUID;
				ushort actionTypeID = _dimse.Command.ActionTypeID;
				Log.Info("{0} <- N-Action request [pc: {1}; id: {2}; class: {3}; action: {4:x4}]",
					LogID, presentationID, messageID, requestedClass, actionTypeID);
				OnReceiveNActionRequest(presentationID, messageID, requestedClass, requestedInstance, actionTypeID, _dimse.Dataset);
				return true;
			}

			if (commandField == DcmCommandField.NActionResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmUID affectedClass = _dimse.Command.AffectedSOPClassUID;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				ushort actionTypeID = _dimse.Command.ActionTypeID;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- N-Action response [id: {1}; class: {2}; action: {3:x4}]: {4}",
					LogID, messageIdRespondedTo, affectedClass, actionTypeID, status);
				OnReceiveNActionResponse(presentationID, messageIdRespondedTo, affectedClass, affectedInstance, actionTypeID, _dimse.Dataset, status);
				return true;
			}

			if (commandField == DcmCommandField.NCreateRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmUID requestedClass = _dimse.Command.RequestedSOPClassUID;
				DcmUID requestedInstance = _dimse.Command.RequestedSOPInstanceUID;
				Log.Info("{0} <- N-Create request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
				OnReceiveNCreateRequest(presentationID, messageID, requestedClass, requestedInstance, _dimse.Dataset);
				return true;
			}

			if (commandField == DcmCommandField.NCreateResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmUID affectedClass = _dimse.Command.AffectedSOPClassUID;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- N-Create response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
				OnReceiveNCreateResponse(presentationID, messageIdRespondedTo, affectedClass, affectedInstance, _dimse.Dataset, status);
				return true;
			}

			if (commandField == DcmCommandField.NDeleteRequest) {
				ushort messageID = _dimse.Command.MessageID;
				DcmUID requestedClass = _dimse.Command.RequestedSOPClassUID;
				DcmUID requestedInstance = _dimse.Command.RequestedSOPInstanceUID;
				Log.Info("{0} <- N-Delete request [pc: {1}; id: {2}; class: {3}]", LogID, presentationID, messageID, requestedClass);
				OnReceiveNDeleteRequest(presentationID, messageID, requestedClass, requestedInstance);
				return true;
			}

			if (commandField == DcmCommandField.NDeleteResponse) {
				ushort messageIdRespondedTo = _dimse.Command.MessageIDRespondedTo;
				DcmUID affectedClass = _dimse.Command.AffectedSOPClassUID;
				DcmUID affectedInstance = _dimse.Command.AffectedSOPInstanceUID;
				DcmStatus status = _dimse.Command.Status;
				Log.Info("{0} <- N-Delete response [id: {1}; class: {2}]: {3}", LogID, messageIdRespondedTo, affectedClass, status);
				OnReceiveNDeleteResponse(presentationID, messageIdRespondedTo, affectedClass, affectedInstance, status);
				return true;
			}

			return false;
		}

		private void SendRawPDU(RawPDU pdu) {
			try {
				_disableTimeout = true;
				pdu.WritePDU(_network);
				_disableTimeout = false;
			}
			catch (Exception e) {
				Log.Error("{0} -> Error sending PDU [type: 0x{1:x2}]: {2}", LogID, pdu.Type, e.Message);
				OnNetworkError(e);
			}
		}

		private bool SendDimse(byte pcid, DcmCommand command, DcmDataset dataset) {
			try {
				_disableTimeout = true;

				DcmTS ts = _assoc.GetAcceptedTransferSyntax(pcid);

				DcmDimseProgress progress = new DcmDimseProgress();

				progress.EstimatedCommandLength = (int)command.CalculateWriteLength(DcmTS.ImplicitVRLittleEndian, DicomWriteOptions.Default | DicomWriteOptions.CalculateGroupLengths);

				if (dataset != null)
					progress.EstimatedDatasetLength = (int)dataset.CalculateWriteLength(ts, DicomWriteOptions.Default);

				PDataTFStream pdustream = new PDataTFStream(_network, pcid, (int)_assoc.MaximumPduLength);
				pdustream.OnPduSent += delegate() {
					progress.BytesTransfered = pdustream.BytesSent;
					OnSendDimseProgress(pcid, command, dataset, progress);
				};

				OnSendDimseBegin(pcid, command, dataset, progress);

				DicomStreamWriter dsw = new DicomStreamWriter(pdustream);
				dsw.Write(command, DicomWriteOptions.Default | DicomWriteOptions.CalculateGroupLengths);

				if (dataset != null) {
					pdustream.IsCommand = false;
					dsw.Write(dataset, DicomWriteOptions.Default);
				}

				// flush last pdu
				pdustream.Flush(true);

				OnSendDimse(pcid, command, dataset, progress);

				return true;
			}
			catch (Exception e) {
				Log.Error("{0} -> Error sending DIMSE: {1}", LogID, e.Message);
				OnNetworkError(e);
				return false;
			}
			finally {
				_disableTimeout = false;
			}
		}

		private bool SendDimseStream(byte pcid, DcmCommand command, Stream datastream) {
			try {
				_disableTimeout = true;

				DcmTS ts = _assoc.GetAcceptedTransferSyntax(pcid);

				DcmDimseProgress progress = new DcmDimseProgress();

				progress.EstimatedCommandLength = (int)command.CalculateWriteLength(DcmTS.ImplicitVRLittleEndian, DicomWriteOptions.Default | DicomWriteOptions.CalculateGroupLengths);

				if (datastream != null)
					progress.EstimatedDatasetLength = (int)datastream.Length - (int)datastream.Position;

				PDataTFStream pdustream = new PDataTFStream(_network, pcid, (int)_assoc.MaximumPduLength);
				pdustream.OnPduSent += delegate() {
					progress.BytesTransfered = pdustream.BytesSent;
					OnSendDimseProgress(pcid, command, null, progress);
				};

				OnSendDimseBegin(pcid, command, null, progress);

				DicomStreamWriter dsw = new DicomStreamWriter(pdustream);
				dsw.Write(command, DicomWriteOptions.Default | DicomWriteOptions.CalculateGroupLengths);
				dsw = null;

				if (datastream != null) {
					pdustream.IsCommand = false;
					pdustream.Write(datastream);
				}

				// flush last pdu
				pdustream.Flush(true);

				OnSendDimse(pcid, command, null, progress);

				return true;
			}
			catch (Exception e) {
				Log.Error("{0} -> Error sending DIMSE: {1}", LogID, e.Message);
				OnNetworkError(e);
				return false;
			}
			finally {
				_disableTimeout = false;
			}
		}
		#endregion
	}
}
