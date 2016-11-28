﻿#if !NOSOCKET
using System.Linq;

namespace nStratis.Protocol.Payloads
{
	/// <summary>
	/// An available peer address in the bitcoin network is announce (unsollicited or after a getaddr)
	/// </summary>
	[Payload("addr")]
	public class AddrPayload : Payload, IBitcoinSerializable
	{
		NetworkAddress[] addr_list = new NetworkAddress[0];
		public NetworkAddress[] Addresses
		{
			get
			{
				return addr_list;
			}
		}

		public AddrPayload()
		{

		}
		public AddrPayload(NetworkAddress address)
		{
			addr_list = new NetworkAddress[] { address };
		}
		public AddrPayload(NetworkAddress[] addresses)
		{
			addr_list = addresses.ToArray();
		}

		#region IBitcoinSerializable Members

		public override void ReadWriteCore(BitcoinStream stream)
		{
			stream.ReadWrite(ref addr_list);
		}

		#endregion

		public override string ToString()
		{
			return Addresses.Length + " address(es)";
		}
	}
}
#endif