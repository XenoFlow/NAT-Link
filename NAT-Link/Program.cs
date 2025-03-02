using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using STUN;
using System.Runtime.CompilerServices;

public class NATPunchthroughClient
{
    private Socket _listener;
    private readonly ConcurrentDictionary<string, TcpClient> _connections = new();
    private CancellationTokenSource _cts = new();

    public async Task StartClientAsync(IPEndPoint StunServer)
    {
        try
        {
            // 获取公网端点信息
            var publicEP = await GetPublicEndPoint(StunServer);
            Console.WriteLine($"Public endpoint: {publicEP.Item1}");

            // 启动本地监听
            _listener = publicEP.Item2.BeginAccept(new AsyncCallback(OnConnect),this);
            //_ = Task.Run(() => ListenForIncomingConnections());

            // 主命令循环
            while (!_cts.IsCancellationRequested)
            {
                Console.Write("> ");
                var input = Console.ReadLine();

                switch (input)
                {
                    case "connect":
                        Console.Write("请输入地址：");
                        var parts = Console.ReadLine().Trim();
                        IPEndPoint res;
                        if (IPEndPoint.TryParse(parts, out res))
                        {
                            await ConnectToPeer(res.Address.ToString(), res.Port);
                        }
                        else
                        {
                            Console.WriteLine("错误的地址格式");
                        }
                        break;
                    case "disconnect":
                        Console.Write("请输入地址：");
                        var addr = Console.ReadLine().Trim();
                        if (_connections.TryRemove(addr, out var client))
                        {
                            client.Close();
                            Console.WriteLine("已断开指定连接");
                        }
                        else
                        {
                            Console.WriteLine("未找到连接");
                        }
                        break;
                    case "list":
                        foreach (var key in _connections.Keys)
                        {
                            Console.WriteLine(key);
                        }
                        break;
                    default:
                        if (input == "exit")
                        {
                            break;
                        }
                        else
                        {
                            await BroadcastMessage(input);
                        }
                        break;
                }
                
            }
        }
        finally
        {
            Cleanup();
        }
    }

    private void OnConnect(IAsyncResult iar)
    {
        Socket client = (Socket)iar.AsyncState;
        try
        {
            var _newSocket =  client.Accept();
            _newSocket.Send(Encoding.UTF8.GetBytes("Accepted"));
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
    }

    //private async Task ListenForIncomingConnections()
    //{
    //    while (!_cts.IsCancellationRequested)
    //    {
    //        var client = await _listener.AcceptTcpClientAsync();
    //        Console.WriteLine($"收到来自 {client.Client.RemoteEndPoint} 的连接请求");

    //        Console.WriteLine("已接受连接请求");
    //        var key = ((IPEndPoint)client.Client.RemoteEndPoint).ToString();
    //        _connections.TryAdd(key, client);
    //        _ = Task.Run(() => HandleClient(client));
    //        await client.Client.SendAsync(Encoding.UTF8.GetBytes("Accepted"));
    //    }
    //}

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var buffer = new byte[4096];
            try
            {
                // 心跳包发送
                var heartbeatTask = Task.Run(async () =>
                {
                    while (client.Connected)
                    {
                        await Task.Delay(30000);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("\u0000"), _cts.Token);
                    }
                });

                while (client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (message == "\u0000") continue;  // 过滤心跳包

                    if (message == "Accepted") Console.WriteLine("对方已同意连接");

                    Console.WriteLine($"Received: {message}");
                }
            }
            catch (IOException)
            {
                Console.WriteLine($"用户 {client.Client.RemoteEndPoint} 断开的自己的连接");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"连接出错： {ex}");
            }
            finally
            {
                _connections.TryRemove(((IPEndPoint)client.Client.RemoteEndPoint).ToString(), out _);
            }
        }
    }

    private async Task ConnectToPeer(string ip, int port)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(ip, port);
            if (client.Connected)
            {
                var key = ((IPEndPoint)client.Client.RemoteEndPoint).ToString();
                _connections.TryAdd(key, client);
                _ = Task.Run(() => HandleClient(client));
                Console.WriteLine("等待对方同意");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"连接失败： {ex.Message}");
            client.Close();
        }
    }

    private async Task BroadcastMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var data = Encoding.UTF8.GetBytes(message);
        foreach (var (key, client) in _connections)
        {
            try
            {
                if (client.Connected)
                {
                    await client.GetStream().WriteAsync(data, 0, data.Length, _cts.Token);
                }
            }
            catch
            {
                _connections.TryRemove(key, out _);
            }
        }
    }

    private async Task<(EndPoint,Socket)> GetPublicEndPoint(IPEndPoint StunServer)
    {
        var res = await STUN.STUNClient.QueryAsync(StunServer, STUN.STUNQueryType.PublicIP,false);
        return (res.PublicEndPoint,res.Socket);
    }

    private void Cleanup()
    {
        _cts.Cancel();
        _listener?.Close();
        foreach (var client in _connections.Values)
        {
            client.Close();
        }
    }

    public static async Task Main(string[] args)
    {
        var client = new NATPunchthroughClient();
        const string STUN_SERVER = "stun.miwifi.com";
        const int STUN_PORT = 3478;
        await client.StartClientAsync(new IPEndPoint(Dns.GetHostAddresses(STUN_SERVER)[0],STUN_PORT));
    }
}