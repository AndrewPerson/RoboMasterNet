using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RoboMaster;

public class PushReceiver : IDisposable
{
    public int Port { get; }

    public UdpClient Client { get; }

    public Feed<ResponseData> Data { get; } = new();

    public PushReceiver(int port)
    {
        Port = port;

        Client = new UdpClient(port);

        new Thread(ReceiveLoop)
        {
            IsBackground = true
        }.Start();
    }

    private void ReceiveLoop()
    {
        var endpoint = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            var data = Client.Receive(ref endpoint);
            var message = Encoding.UTF8.GetString(data);

            Data.Notify(ResponseData.Parse(message));
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        GC.SuppressFinalize(this);
    }    
}