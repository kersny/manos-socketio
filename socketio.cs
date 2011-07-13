using Manos;
using Manos.Http;
using Manos.Routing;
using Manos.IO;
using System;
using WebSockets;

namespace SocketIO {

	public class socketio : ManosApp {

		public socketio ()
		{
			Route ("/Content/", new StaticContentModule ());
			Route("", new WebSocketServer("/socket.io/1/websocket/{id}", OnConnect));
			Route("/socket.io/1/", HandShake);
			//Route("", new XHRPollingServer("/socket.io/1/xhr-polling/{id}", OnConnect));
		}
		public void OnConnect(WebSocket sock)
		{
			sock.Send("1::");
			var time = AppHost.AddTimeout(new TimeSpan(0, 0, 12), new InfiniteRepeatBehavior(), null, (app, data) => { sock.Send("2::"); });
			sock.OnData += (msg) => {
				Console.WriteLine("Received Message: {0}", System.Text.Encoding.UTF8.GetString(msg.Bytes, msg.Position, msg.Length));
				HandleMessage(msg, sock);
			};
		}
		public void HandShake(IManosContext ctx)
		{
			var id = GenerateId();	
			int heartbeat_timeout = 15;
			int close_timeout = 25;
			string[] supported_transports = new string[] { "websocket" }; //websocket
			ctx.Response.End(id + ":" + heartbeat_timeout.ToString() + ":" + close_timeout.ToString() + ":" + string.Join(",", supported_transports));
		}
		public void HandleMessage(ByteBuffer message, WebSocket reciever)
		{
			switch ((int)message.CurrentByte)
			{
				case 1:
					//Connect
					//Example: 1::
					//or 1::/text?my=param
					//Echo to Acknowledge
					reciever.Send(message);
					break;
				case 2:
					//Heartbeat
					//Should do timeout disconnection and removal
					//Echo to Acknowledge
					reciever.Send(message);	
					break;
				case 3:
					//Message
					//Example: 3:1::blabla
					//3:[id]:[endpoint]:[data]
					Console.WriteLine(message.ToString());
					break;
				case 4:
					//JSON Message
					//Example: 4:1::{"a":"b"}
					//4:[id]:[endpoint]:[json]
					break;
				case 5:
					//Event
					//Same as JSON
					//Mandantory string name and array args
					break;
				case 6:
					//ACK
					//6:::4
					//6:::4+["A","B"]	
					//Not exactly sure on this one
					break;
				case 7:
					//Error
					//7::[endpoint]:[reason]+[advice]
					break;
				case 8:
					//Noop
					break;
			}
		}
		public string GenerateId()
		{
			return System.Guid.NewGuid().ToString().Replace("-", "");
		}
	}
}
