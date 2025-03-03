using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Mono.Nat;
using STUN;

class Program
{
    static string publicIP;
    static int publicPort;
    static Socket udpSocket; // 改为直接使用Socket
    static bool isMapped = false;

    static void Main(string[] args)
    {
        // 创建并绑定Socket到随机端口
        udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0)); // 随机端口
        int localPort = ((IPEndPoint)udpSocket.LocalEndPoint).Port;

        // 使用STUN获取公网IP和端口
        GetPublicInfoWithSTUN();

        // 使用Mono.Nat尝试UPnP端口映射（保持原逻辑）
        TryUPnPMapping(localPort);

        // 用户交互交换地址信息
        Console.WriteLine($"请将您的公网地址发送给他人: {publicIP}:{publicPort}");
        Console.Write("请输入对方公网地址 (IP:端口): ");
        string remoteInput = Console.ReadLine().Trim();
        IPEndPoint remoteEndPoint;
        if (!IPEndPoint.TryParse(remoteInput,out remoteEndPoint))
        {
            Console.WriteLine("地址格式错误，程序退出");
            return;
        }

        // 启动接收线程
        Thread receiveThread = new Thread(ReceiveMessages);
        receiveThread.IsBackground = true;
        receiveThread.Start();

        // 发送打孔数据包
        Console.WriteLine("正在尝试NAT打孔...");
        byte[] punchPacket = System.Text.Encoding.UTF8.GetBytes("STATUS:PUNCH");
        udpSocket.SendTo(punchPacket, remoteEndPoint);


        // 后续通信...
        while (true)
        {
            string message = Console.ReadLine();
            switch (message)
            {
                case "exit":
                    SendMessage("STATUS:CLOSE", remoteEndPoint);
                    return;
                default:
                    SendMessage($"MSG:{message}", remoteEndPoint);
                    break;
            }
        }
    }

    static void SendMessage(string message, IPEndPoint remoteEP)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes($"{message}");
        udpSocket.SendTo(data, remoteEP);
    }

    static void GetPublicInfoWithSTUN()
    {
        try
        {
            // 解析STUN服务器地址（使用Google STUN）
            IPAddress[] stunIPs = Dns.GetHostAddresses("stun.miwifi.com");
            IPEndPoint stunServer = new IPEndPoint(stunIPs[0], 3478);

            // 创建STUN客户端（直接使用Socket）
            var queryResult = STUNClient.Query(stunServer, STUNQueryType.OpenNAT, false);
            // 检查结果状态
            if (queryResult is null)
            {
                throw new Exception("STUN查询失败");
            }
            udpSocket = queryResult.Socket;


            // 获取公网地址
            publicIP = queryResult.PublicEndPoint.Address.ToString();
            publicPort = queryResult.PublicEndPoint.Port;
            if (queryResult.NATType >= STUNNATType.Symmetric)
            {
                Console.WriteLine("你的 NAT 类型很差，打洞很有可能失败……");
            }
            Console.WriteLine($"NAT类型: {queryResult.NATType}");
            Console.WriteLine($"公网地址: {publicIP}:{publicPort}");
        }
        catch (Exception ex)
        {   
            Console.WriteLine($"STUN错误: {ex.Message}");
            // 回退到本地地址
            IPEndPoint localEP = (IPEndPoint)udpSocket.LocalEndPoint;
            publicIP = localEP.Address.ToString();
            publicPort = localEP.Port;
        }
    }

    static void TryUPnPMapping(int localPort)
    {
        NatUtility.DeviceFound += (sender, args) =>
        {
            INatDevice device = args.Device;
            try
            {
                device.CreatePortMap(new Mapping(Protocol.Udp, localPort, publicPort, 3600, "P2P Hole Punching"));
                isMapped = true;
                Console.WriteLine($"UPnP映射成功: {localPort} -> {publicPort}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UPnP映射失败: {ex.Message}");
            }
        };
        NatUtility.StartDiscovery();
    }

    static void ReceiveMessages()
    {
        byte[] buffer = new byte[1024];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            try
            {
                int received = udpSocket.ReceiveFrom(buffer, ref remoteEP);
                string message = System.Text.Encoding.UTF8.GetString(buffer, 0, received);

                if (message.StartsWith("STATUS"))
                {
                    string status = message.Substring(7);
                    switch (status)
                    {
                        case "PUNCH":
                            ShowMessageToUser(remoteEP,"已连接（打洞成功）");
                            SendMessage("STATUS:CONNECTED", (IPEndPoint)remoteEP);
                            break;
                        case "CONNECTED":
                            ShowMessageToUser(remoteEP,"已连接");
                            break;
                        case "CLOSE":
                            ShowMessageToUser(remoteEP,"已关闭连接");
                            break;
                    }
                }
                if (message.StartsWith("MSG"))
                {
                    string msg = message.Substring(4);
                    ShowMessageToUser(remoteEP, $"[MSG] {msg}");
                }
            }
            catch (SocketException ex)
            {
                ShowMessageToUser(remoteEP, $"接收错误: {ex.SocketErrorCode}");
            }
        }
    }

    static void ShowMessageToUser(EndPoint from,string message)
    {
        Console.WriteLine($"[{DateTime.Now}] [{from}] {message}");
    }
}