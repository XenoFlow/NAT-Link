using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Security.Cryptography;

class Program
{
    static async Task Main(string[] args)
    {
        var client = new P2PClient();
        await client.StartAsync();
    }
}

public class StunClient
{
    // STUN 消息类型
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;

    // STUN 属性类型
    private const ushort MappedAddress = 0x0001;
    private const ushort XorMappedAddress = 0x0020;

    // STUN Magic Cookie
    private static readonly byte[] MagicCookie = { 0x21, 0x12, 0xA4, 0x42 };

    public async static Task<IPEndPoint> GetPublicEndPoint(string stunServer = "stun.miwifi.com", int port = 3478)
    {
        using (var udpClient = new UdpClient())
        {
            udpClient.Connect(stunServer, port);

            // 创建 STUN 绑定请求
            var request = CreateBindingRequest();
            await udpClient.SendAsync(request, request.Length);

            // 设置超时时间（3秒）
            var asyncResult = udpClient.BeginReceive(null, null);
            asyncResult.AsyncWaitHandle.WaitOne(3000);
            if (!asyncResult.IsCompleted)
            {
                throw new TimeoutException("STUN server response timed out");
            }

            // 接收响应
            IPEndPoint remoteEP = null;
            byte[] response = udpClient.EndReceive(asyncResult, ref remoteEP);

            return ParseStunResponse(response);
        }
    }

    private static byte[] CreateBindingRequest()
    {
        byte[] transactionId = GenerateTransactionId();

        var message = new byte[20]; // STUN 头部长度 20 字节
        // 消息类型（Binding Request）
        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)BindingRequest)), 0, message, 0, 2);
        // 消息长度（无属性）
        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)0)), 0, message, 2, 2);
        // Magic Cookie
        Buffer.BlockCopy(MagicCookie, 0, message, 4, 4);
        // Transaction ID (12 bytes)
        Buffer.BlockCopy(transactionId, 0, message, 8, 12);

        return message;
    }

    private static IPEndPoint ParseStunResponse(byte[] response)
    {
        // 验证响应头
        if (response.Length < 20)
            throw new ArgumentException("Invalid STUN response");

        // 检查 Magic Cookie
        for (int i = 4; i < 8; i++)
        {
            if (response[i] != MagicCookie[i - 4])
                throw new ArgumentException("Invalid STUN response");
        }

        int offset = 20; // 跳过头部

        while (offset < response.Length)
        {
            ushort attributeType = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(response, offset));
            ushort attributeLength = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(response, offset + 2));
            offset += 4;

            if (attributeType == XorMappedAddress || attributeType == MappedAddress)
            {
                return ParseAddressAttribute(response, offset, attributeLength);
            }

            offset += attributeLength;
        }

        throw new ArgumentException("No valid address found in STUN response");
    }

    private static IPEndPoint ParseAddressAttribute(byte[] data, int offset, ushort length)
    {
        int port = data[offset + 2] << 8 | data[offset + 3];
        byte[] addressBytes;

        // IPv4
        if (data[offset + 1] == 0x01)
        {
            addressBytes = new byte[4];
            Buffer.BlockCopy(data, offset + 4, addressBytes, 0, 4);

            // 如果是 XOR-MAPPED-ADDRESS，需要异或处理
            if (data[offset] == 0x00 && data[offset + 1] == 0x20)
            {
                port ^= (MagicCookie[0] << 8) | MagicCookie[1];
                for (int i = 0; i < 4; i++)
                    addressBytes[i] ^= MagicCookie[i];
            }

            return new IPEndPoint(new IPAddress(addressBytes), port);
        }

        // IPv6（示例未完全实现）
        throw new NotSupportedException("IPv6 is not supported in this example");
    }

    private static byte[] GenerateTransactionId()
    {
        var id = new byte[12];
        new Random().NextBytes(id);
        return id;
    }
}

public class P2PClient
{
    private readonly ConcurrentDictionary<string, QuicConnection> connections = new();
    private QuicListener? listener;
    private IPEndPoint? publicEndPoint;
    private X509Certificate2 serverCertificate;

    public P2PClient()
    {
        // 生成自签名证书（生产环境应使用正式证书）
        serverCertificate = GenerateSelfSignedCertificate();
    }

    public async Task StartAsync()
    {
        // 获取公网地址
        publicEndPoint = await StunClient.GetPublicEndPoint();
        Console.WriteLine($"Your public address: {publicEndPoint}");

        // 启动 QUIC 监听
        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Any, publicEndPoint.Port),
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3
            },
            ConnectionOptionsCallback = (_, _, _) =>
            {
                var serverOptions = new QuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCertificate,
                        ClientCertificateRequired = false
                    }
                };
                return ValueTask.FromResult(serverOptions);
            }
        };

        listener = await QuicListener.ListenAsync(listenerOptions);
        _ = ListenForConnectionsAsync();

        while (true)
        {
            Console.WriteLine("\nCommands: [connect] [list] [send] [disconnect] [exit]");
            var input = Console.ReadLine()?.Trim().ToLower();

            switch (input)
            {
                case "connect":
                    await HandleConnectCommand();
                    break;
                case "list":
                    ListConnections();
                    break;
                case "send":
                    await HandleSendCommand();
                    break;
                case "disconnect":
                    HandleDisconnectCommand();
                    break;
                case "exit":
                    return;
            }
        }
    }

    private async Task ListenForConnectionsAsync()
    {
        while (true)
        {
            try
            {
                var connection = await listener.AcceptConnectionAsync();
                _ = HandleIncomingConnection(connection);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Accept connection failed: {ex.Message}");
            }
        }
    }

    private async Task HandleIncomingConnection(QuicConnection connection)
    {
        var endpoint = connection.RemoteEndPoint?.ToString();
        Console.WriteLine($"Incoming connection from {endpoint}. Accept? [Y/N]");

        if (Console.ReadKey(true).Key == ConsoleKey.Y)
        {
            connections[endpoint!] = connection;
            _ = ReceiveMessagesAsync(connection);
            Console.WriteLine($"Connected to {endpoint}");
        }
        else
        {
            await connection.CloseAsync(0);
        }
    }

    private async Task HandleConnectCommand()
    {
        Console.Write("Enter remote address (IP:port): ");
        var address = Console.ReadLine();

        if (!IPEndPoint.TryParse(address, out var remoteEP))
        {
            Console.WriteLine("Invalid address format");
            return;
        }

        try
        {
            var clientOptions = new QuicClientConnectionOptions
            {
                RemoteEndPoint = remoteEP,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http3
        },
                    RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    {
                        // 这里可以添加自定义证书验证逻辑
                        return errors == SslPolicyErrors.None;
                    }
                },
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1
            };

            var connection = await QuicConnection.ConnectAsync(clientOptions);
            connections[remoteEP.ToString()!] = connection;
            _ = ReceiveMessagesAsync(connection);
            Console.WriteLine($"Connected to {remoteEP}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection failed: {ex.Message}");
        }
    }

    private async Task ReceiveMessagesAsync(QuicConnection connection)
    {
        var endpoint = connection.RemoteEndPoint?.ToString();

        try
        {
            while (true)
            {
                await using var stream = await connection.AcceptInboundStreamAsync();
                var buffer = new byte[1024];

                while (true)
                {
                    var bytesRead = await stream.ReadAsync(buffer);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Console.WriteLine($"\n[{endpoint}]: {message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection error ({endpoint}): {ex.Message}");
        }

        Console.WriteLine($"\nConnection closed: {endpoint}");
        connections.TryRemove(endpoint!, out _);
        await connection.CloseAsync(0);
    }

    private async Task HandleSendCommand()
    {
        Console.Write("Enter message: ");
        var message = Console.ReadLine();

        Console.Write("Recipient address (leave empty for all): ");
        var recipient = Console.ReadLine();

        var data = Encoding.UTF8.GetBytes(message!);

        foreach (var (endpoint, connection) in connections)
        {
            if (string.IsNullOrEmpty(recipient) || endpoint == recipient)
            {
                try
                {
                    // 修改此行，添加流类型参数
                    await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional);
                    await stream.WriteAsync(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to send to {endpoint}: {ex.Message}");
                }
            }
        }
    }

    private void ListConnections()
    {
        Console.WriteLine("Active connections:");
        foreach (var endpoint in connections.Keys)
        {
            Console.WriteLine($"- {endpoint}");
        }
    }

    private void HandleDisconnectCommand()
    {
        Console.Write("Enter address to disconnect: ");
        var address = Console.ReadLine();

        if (connections.TryRemove(address!, out var connection))
        {
            // 修改此处为异步等待
            connection.CloseAsync(0).ConfigureAwait(false).GetAwaiter().GetResult();
            Console.WriteLine($"Disconnected from {address}");
        }
        else
        {
            Console.WriteLine("Connection not found");
        }
    }

    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var certRequest = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var certificate = certRequest.CreateSelfSigned(
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddYears(1));

        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }
}