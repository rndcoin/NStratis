﻿#if !NOSOCKET
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin.Protocol.Behaviors;

namespace NBitcoin.Protocol
{
	public class NodesGroup : IDisposable
	{
		TraceCorrelation _Trace = new TraceCorrelation(NodeServerTrace.Trace, "Group connection");
		NodeConnectionParameters _ConnectionParameters;
		public NodeConnectionParameters NodeConnectionParameters
		{
			get
			{
				return _ConnectionParameters;
			}
			set
			{
				_ConnectionParameters = value;
			}
		}

		NodeRequirement _Requirements;
		CancellationTokenSource _Disconnect;
		Network _Network;
		object cs;
		object tcs;

		public NodesGroup(
			Network network,
			NodeConnectionParameters connectionParameters = null,
			NodeRequirement requirements = null)
		{
			AllowSameGroup = false;
			MaximumNodeConnection = 8;
			_Network = network;
			cs = new object();
			tcs = new object();
			_ConnectedNodes = new NodesCollection();
			_ConnectionParameters = connectionParameters ?? new NodeConnectionParameters();
			_ConnectionParameters = _ConnectionParameters.Clone();
			_Requirements = requirements ?? new NodeRequirement();
			_Disconnect = new CancellationTokenSource();
		}

		/// <summary>
		/// Start connecting asynchronously to remote peers
		/// </summary>
		public void Connect()
		{
			_Disconnect = new CancellationTokenSource();
			StartConnecting();
		}
		/// <summary>
		/// Drop connection to all connected nodes
		/// </summary>
		public void Disconnect()
		{
			_Disconnect.Cancel();
			_ConnectedNodes.DisconnectAll();
		}

		readonly AddressManager _DefaultAddressManager = new AddressManager();
		volatile bool _Connecting;

		/// <summary>
		/// Try to connect to a single endpoint
		/// The connected endpoint will be added to the nodes collection
		/// </summary>
		/// <param name="force">Connect even if MaximumNodeConnection limit was reached</param>
		/// <returns>A connected node or null</returns>
		public Node TryConnectNode(IPEndPoint endPoint, bool force = false)
		{
			// this is not expected to be performance critical so
			// only use a single lock. placing a lock before the 
			// check should avoid connecting to a node already connected 
			lock (tcs)
			{
				// first look for the node maybe its already connected
				var node = this.ConnectedNodes.FindByEndpoint(endPoint);
				if (node != null)
					return node;

				if (!force && _ConnectedNodes.Count >= MaximumNodeConnection)
					return null;

				var scope = _Trace.Open();

				NodeServerTrace.Information("Connected nodes : " + _ConnectedNodes.Count + "/" + MaximumNodeConnection);
				var parameters = _ConnectionParameters.Clone();
				parameters.TemplateBehaviors.Add(new NodesGroupBehavior(this));
				parameters.ConnectCancellation = _Disconnect.Token;

				try
				{
					node = Node.Connect(_Network, endPoint, parameters);
					var timeout = CancellationTokenSource.CreateLinkedTokenSource(_Disconnect.Token);
					timeout.CancelAfter(5000);
					node.VersionHandshake(_Requirements, timeout.Token);
					NodeServerTrace.Information("Node successfully connected to and handshaked");
				}
				catch (OperationCanceledException ex)
				{
					if (_Disconnect.Token.IsCancellationRequested)
						throw;
					NodeServerTrace.Error("Timeout for picked node", ex);
					if (node != null)
						node.DisconnectAsync("Handshake timeout", ex);
				}
				catch (SocketException)
				{
					_ConnectionParameters.ConnectCancellation.WaitHandle.WaitOne(500);
				}
				catch (Exception ex)
				{
					NodeServerTrace.Error("Error while connecting to node", ex);
					if (node != null)
						node.DisconnectAsync("Error while connecting", ex);
				}
				finally
				{
					scope?.Dispose();
				}

				return node;
			}
		}

		internal void StartConnecting()
		{
			if(_Disconnect.IsCancellationRequested)
				return;
			if(_ConnectedNodes.Count >= MaximumNodeConnection)
				return;
			if(_Connecting)
				return;
			Task.Factory.StartNew(() =>
			{
				if(Monitor.TryEnter(cs))
				{
					_Connecting = true;
					TraceCorrelationScope scope = null;
					try
					{
						while(!_Disconnect.IsCancellationRequested && _ConnectedNodes.Count < MaximumNodeConnection)
						{
							scope = scope ?? _Trace.Open();

							NodeServerTrace.Information("Connected nodes : " + _ConnectedNodes.Count + "/" + MaximumNodeConnection);
							var parameters = _ConnectionParameters.Clone();
							parameters.TemplateBehaviors.Add(new NodesGroupBehavior(this));
							parameters.ConnectCancellation = _Disconnect.Token;
							var addrman = AddressManagerBehavior.GetAddrman(parameters);

							if(addrman == null)
							{
								addrman = _DefaultAddressManager;
								AddressManagerBehavior.SetAddrman(parameters, addrman);
							}

							Node node = null;
							try
							{
								node = Node.Connect(_Network, parameters, AllowSameGroup ? null : _ConnectedNodes.Select(n => n.RemoteSocketAddress).ToArray());
								var timeout = CancellationTokenSource.CreateLinkedTokenSource(_Disconnect.Token);
								timeout.CancelAfter(5000);
								node.VersionHandshake(_Requirements, timeout.Token);
								NodeServerTrace.Information("Node successfully connected to and handshaked");
							}
							catch(OperationCanceledException ex)
							{
								if(_Disconnect.Token.IsCancellationRequested)
									throw;
								NodeServerTrace.Error("Timeout for picked node", ex);
								if(node != null)
									node.DisconnectAsync("Handshake timeout", ex);
							}
							catch(Exception ex)
							{
								NodeServerTrace.Error("Error while connecting to node", ex);
								if(node != null)
									node.DisconnectAsync("Error while connecting", ex);
							}

						}
					}
					finally
					{
						Monitor.Exit(cs);
						_Connecting = false;
						if(scope != null)
							scope.Dispose();
					}
				}
			}, TaskCreationOptions.LongRunning);
		}


		public static NodesGroup GetNodeGroup(Node node)
		{
			return GetNodeGroup(node.Behaviors);
		}
		public static NodesGroup GetNodeGroup(NodeConnectionParameters parameters)
		{
			return GetNodeGroup(parameters.TemplateBehaviors);
		}
		public static NodesGroup GetNodeGroup(NodeBehaviorsCollection behaviors)
		{
			return Enumerable.OfType<NodesGroupBehavior>(behaviors).Select(c => c._Parent).FirstOrDefault();
		}

		/// <summary>
		/// Asynchronously create a new set of nodes
		/// </summary>
		public void Purge(string reason)
		{
			Task.Factory.StartNew(() =>
			{
				var initialNodes = _ConnectedNodes.ToDictionary(n => n);
				while(!_Disconnect.IsCancellationRequested && initialNodes.Count != 0)
				{
					var node = initialNodes.First();
					node.Value.Disconnect(reason);
					initialNodes.Remove(node.Value);
					_Disconnect.Token.WaitHandle.WaitOne(5000);
				}
			});
		}

		/// <summary>
		/// The number of node that this behavior will try to maintain online (Default : 8)
		/// </summary>
		public int MaximumNodeConnection
		{
			get;
			set;
		}

		public NodeRequirement Requirements
		{
			get
			{
				return _Requirements;
			}
			set
			{
				_Requirements = value;
			}
		}

		internal NodesCollection _ConnectedNodes;
		public NodesCollection ConnectedNodes
		{
			get
			{
				return _ConnectedNodes;
			}
		}

		/// <summary>
		/// If false, the search process will do its best to connect to Node in different network group to prevent sybil attacks (Default : false)
		/// </summary>
		public bool AllowSameGroup
		{
			get;
			set;
		}

		#region IDisposable Members


		/// <summary>
		/// Same as Disconnect
		/// </summary>
		public void Dispose()
		{
			Disconnect();
		}

		#endregion
	}
}
#endif