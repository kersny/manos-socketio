using Manos;
using Manos.Http;
using Manos.IO;

using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace WebSockets {
	public class WebSocketServer : ManosModule
	{
		public event Action<WebSocket> OnConnect;
		private static List<WebSocket> connections;

		public WebSocketServer(string path, Action<WebSocket> connect)
			: this(path)
		{
			this.OnConnect += connect;
		}
		public WebSocketServer(string path)
		{
			Route(path, (ctx) => {
				var ws = new WebSocket(ctx);
				ws.OnConnect += () => {
					Connections.Add(ws);
					if (OnConnect != null)
						OnConnect(ws);
				};
			});
		}
		public static List<WebSocket> Connections {
			get
			{
				if (connections == null)
					connections = new List<WebSocket>();
				return connections;
			}
		}
	}
	public class WebSocket
	{
		public event Action OnConnect;
		IManosContext context;
		public event Action<ByteBuffer> OnData;

		private static byte[] end = new byte[] { 0xFF };
		private static byte[] start = new byte[] { 0x00 };
		private static string header = "GET {0} HTTP/1.1\r\n" +
					       "Upgrade: WebSocket\r\n" +
					       "Connection: Upgrade\r\n" +
   					       "Sec-WebSocket-Key1: {1}\r\n" +
		                   "Sec-WebSocket-Key2: {2}\r\n" +
	                       "Host: {3}\r\n" +
	     				   "Origin: {4}\r\n\r\n";

		bool connected = false;
		public Uri ConnectionPath { get; set; }
		
		private Socket sock;
		public WebSocket(IManosContext ctx)
		{
			if (ctx.Request.Headers["Connection"] == null || ctx.Request.Headers["Connection"] != "Upgrade" || ctx.Request.Headers["Upgrade"] == null || ctx.Request.Headers["Upgrade"] != "WebSocket")
				throw new Exception("Non-Websocket connection on Websocket Endpoint");
			this.context = ctx;	
			this.WriteResponse();
			(this.context.Request as HttpEntity).OnUpgradeData += (bytes) => {
				if (bytes.Bytes[0] != 0x00 || bytes.Bytes[bytes.Length - 1] != 0xFF) return;
				if (OnData != null)
				{
					OnData(new ByteBuffer(bytes.Bytes, bytes.Position + 1, bytes.Length - 2));
				}
			};
		}
		public Socket Socket {
			get
			{
				return sock;
			}
		}
		public WebSocket(string path)
		{
			this.ConnectionPath = new Uri(path);
		}
		private void WriteHeaders(ConnectionHandshake handshake)
		{
			var stream = this.sock.GetSocketStream();
			var host = ConnectionPath.Host + ((ConnectionPath.Port != 80 || ConnectionPath.Port != 443) ? ":" + ConnectionPath.Port.ToString() : "");
			var origin = ConnectionPath.Scheme + "://" + ConnectionPath.Host + ((ConnectionPath.Port != 80 || ConnectionPath.Port != 443) ? ":" + ConnectionPath.Port.ToString() : "");
			var headerstr = string.Format(header, ConnectionPath.AbsolutePath, handshake.Key1, handshake.Key2, host, origin);
			byte[] h = Encoding.UTF8.GetBytes(headerstr);
			byte[] all = new byte[h.Length + handshake.FinalKey.Length];
			Array.Copy(h, all, h.Length);
			Array.Copy(handshake.FinalKey, 0, all, h.Length, handshake.FinalKey.Length);
			stream.Write(all);
		}
		public void Connect(Action connected)
		{
			this.sock = AppHost.Context.CreateSocket();
			sock.Connect(ConnectionPath.Host, (ConnectionPath.Port == -1) ? ConnectionPath.Scheme == "ws" ? 80 : 443 : ConnectionPath.Port, () => {
				var hand = ConnectionHandshake.Generate();
				this.WriteHeaders(hand);
				sock.GetSocketStream().Read((data) => {
					if (this.connected) {
						if (data.Bytes[0] != 0x00 || data.Bytes[data.Length - 1] != 0xFF) return;
						if (OnData != null)
						{
							OnData(new ByteBuffer(data.Bytes, data.Position + 1, data.Length - 2));
						}
					} else {
						if (this.EnsureResponse(data, hand))
						{
							this.connected = true;
							connected();
						}
						else
						{
							this.sock.Close();
						}
					}
				}, (e) => {}, () => {});
			});
		}
		private bool EnsureResponse(ByteBuffer data, ConnectionHandshake handie)
		{
			bool good = true;
			int pos = data.Length - 16;
			for (int i = 0; i < 16; i++)
			{
				good = data.Bytes[pos + i] == handie.Hash[i];
			}
			return good;
		}
		public void Send(string data)
		{
			Send(Encoding.ASCII.GetBytes(data));
		}
		public void Send(byte[] data)
		{
			Send(new ByteBuffer(data, 0, data.Length));
		}
		public void Send(ByteBuffer data)
		{
			var stream = this.sock.GetSocketStream();
			stream.Write(start);
			stream.Write(data);
			stream.Write(end);
		}
		private void WriteResponse()
		{
			var httpr = this.context.Request as HttpRequest;
			this.context.Response.Stream.Chunked = false;
			this.context.Response.StatusCode = 101;
			this.context.Response.Headers.SetHeader("Upgrade", "WebSocket");
			this.context.Response.Headers.SetHeader("Connection", "Upgrade");
			this.context.Response.Headers.SetHeader("Sec-WebSocket-Origin", this.context.Request.Headers["Origin"]);
			bool secure = this.context.Request.Headers["Origin"].Contains("https");
			string toreplace = secure ? "https" : "http";
			string replacewith = secure ? "wss" : "ws";
			this.context.Response.Headers.SetHeader("Sec-WebSocket-Location", this.context.Request.Headers["Origin"].Replace(toreplace, replacewith) + httpr.Path);
			string key1 = this.context.Request.Headers["Sec-WebSocket-Key1"];
			string key2 = this.context.Request.Headers["Sec-WebSocket-Key2"];
			byte[] end = this.context.Request.GetProperty<byte[]>("UPGRADE_HEAD");
			byte[] ret = ConnectionHandshake.GenerateHandshake(key1, key2, end);
			this.context.Response.Write(ret);
			this.context.Response.OnEnd += () => {
				connected = true;
				this.sock = (this.context.Response as HttpResponse).Socket;
				if (OnConnect != null) {
					OnConnect();
				}
			};
			this.context.Response.End();
		}
		public void Broadcast(string data)
		{
			this.Broadcast(Encoding.ASCII.GetBytes(data));
		}
		public void Broadcast(byte[] data)
		{
			this.Broadcast(new ByteBuffer(data, 0, data.Length));
		}
		public void Broadcast(ByteBuffer data)
		{
			foreach (var socket in WebSocketServer.Connections)
			{
				if (socket != this)
				{
					socket.Send(data);
				}
			}
		}
	}
	public class ConnectionHandshake
	{
		private static string RandomChar(Random r)
		{
			int num = r.Next(33, 126);
			if (num > 47 && num < 58)
				num += 10;
			return ((char)num).ToString();
		}
		public static ConnectionHandshake Generate()
		{
			var r = new Random();
			var ret = new ConnectionHandshake();
			var k1 = handshake_key();
			var k2 = handshake_key();
			ret.Key1 = k1.Item2;
			ret.Key2 = k2.Item2;
			byte[] rand = new byte[8];
			for (int i = 0; i < 8; i++)
			{
				rand[i] = (byte) r.Next(0, 255);
			}
			ret.FinalKey = rand;
			ret.Hash = handshake(k1.Item2, k2.Item2, rand);
			return ret;
		}
		private static Tuple<string, string> handshake_key()
		{
			var r = new Random();
			int spaces1 = r.Next(1, 12);
			uint max1 = (uint)Math.Floor((4294967295F/(float)spaces1));
			int number1 = (int)(r.NextDouble() * (max1 - 1) + 1);
			//int number1 = r.Next(1, (int)max1);
			long product1 = (long)number1 * (long)spaces1;
			string key1 = product1.ToString();
			int numchars = r.Next(1, 12);
			for (int i = 0; i < numchars; i++)
			{
				int index = r.Next(1, key1.Length);
				key1 = key1.Insert(index, RandomChar(r));
			}
			for (int i = 0; i < spaces1; i++)
			{
				int index = r.Next(1, key1.Length);
				key1 = key1.Insert(index, " ");
			}
			return Tuple.Create(product1.ToString(), key1);
		}
		public string Key1 { get; set; }
		public string Key2 { get; set; }
		public byte[] FinalKey { get; set; }
		public byte[] Hash { get; set; }
		public ConnectionHandshake()
		{
		}
		public static byte[] GenerateHandshake(string key1, string key2, byte[] finalkey)
		{
			return handshake(key1, key2, finalkey);
		}
		internal static byte[] handshake(string key1, string key2, byte[] endkey)
		{
			byte[] a1 = stringtobytes(key1);
			byte[] a2 = stringtobytes(key2);
			byte[] ret = new byte[16];
			Array.Copy(a1, 0, ret, 0, 4);
			Array.Copy(a2, 0, ret, 4, 4);
			Array.Copy(endkey, 0, ret, 8, 8);
			return MD5.Create().ComputeHash(ret);
		}
		internal static byte[] stringtobytes(string key)
		{
			//long computed_num = long.Parse(new string(key.Where(x => x >= '0' && x <= '9').ToArray()));
			int unused;
			long computed_num = long.Parse(new string(key.Where(x => int.TryParse(x.ToString(), out unused)).ToArray()));
			int space_count = key.Count(x => x == ' ');
			int ans = (int)(computed_num/space_count);
			return BitConverter.GetBytes(ans).Reverse().ToArray();
		}
	}
}
