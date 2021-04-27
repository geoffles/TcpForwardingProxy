using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SocketTest
{
    class Proxy
    {
        static void Log(int level, string m)
        {
            Console.WriteLine(m);
        }

        private ProxyEdge _listener;
        private ProxyEdge _forwarder;

        public void Run(EndPoint targetEndpoint)
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            IPAddress ipAddress = host.AddressList[0];
            IPEndPoint localEndPoint = new IPEndPoint(ipAddress, 8010);

            Socket socketListener = new Socket(SocketType.Stream, ProtocolType.Tcp);
            
            socketListener.Bind(localEndPoint);

            Log(1,"Start listening");
            socketListener.Listen(1);

            while (true)
            {
                try
                {
                    using (Socket target = new Socket(SocketType.Stream, ProtocolType.Tcp))
                    {
                        Log(1, "Client connect target");
                        target.Connect(targetEndpoint);
                        
                        Log(1, "Waiting for listener connect");
                        using (var source = socketListener.Accept())
                        {
                            
                            var sem = new SemaphoreSlim(2);
                            _listener = new ProxyEdge("Listener", source, target, sem, 2048);
                            _forwarder = new ProxyEdge("Forwarder", target, source, sem, 2048);

                            Log(1, "Starting edges");
                            _listener.Start();
                            _forwarder.Start();

                            sem.Wait();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }
    }

    class ProxyEdge
    {
        static void Log(int level, string m)
        {
            Console.WriteLine(m);
        }

        static object _lock = new object();
        private readonly Socket _source;
        private readonly Socket _target;
        private readonly SemaphoreSlim _semaphor;
        private readonly int _bufsize;
        private readonly string _label;

        public ProxyEdge(string label, Socket source, Socket target, SemaphoreSlim semaphore, int bufsize)
        {
            _label = label;
            _source = source;
            _target = target;
            _semaphor = semaphore;
            _bufsize = bufsize;
        }

        public void Start()
        {
            Log(1, $"{_label} waiting for lock.");

            _semaphor.Wait();
            Log(1, $"{_label} begin run.");

            new Thread(() => Run()).Start();
        }

        void Run()
        {
            while (_source.Connected)
            {
                byte[] bytes = new byte[_bufsize];
                Log(1, $"{_label} waiting for data");
                int bytesRec = _source.Receive(bytes);
                            
                lock(_lock) 
                {
                    if (bytesRec > 0)
                    {
                        if (_target.Connected)
                        {
                            Log(1, $"{_label} forwarding {bytesRec} bytes");
                            _target.Send(bytes);
                            Log(1, $"{_label} sent {bytesRec} bytes");
                        }
                        else
                        {
                            Log(1, $"{_label} target down. {bytesRec} bytes discarded");
                            _source.Shutdown(SocketShutdown.Both);
                            break;
                        }
                    }
                    else
                    {
                        Log(1, $"{_label} source recieved 0. {bytesRec} bytes discarded");
                        _source.Shutdown(SocketShutdown.Both);
                        break;
                    }
                }
            }
            
            Log(1, $"{_label} source disconnected");
            if (_target.Connected)
            {
                Log(1, $"{_label} target shutdown");

                _target.Shutdown(SocketShutdown.Both);
            }

            Log(1, $"{_label} release lock");

            _semaphor.Release();
        }
    }


    class Program
    {
        public static object _lock = new object();



        static void Main(string[] args)
        {
            var proxy = new Proxy();

            proxy.Run(new IPEndPoint(new IPAddress(new byte[]{127,0,1,1}), 9999));
        }
    

        private static void OldLoops()
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            IPAddress ipAddress = host.AddressList[0];
            IPEndPoint localEndPoint = new IPEndPoint(ipAddress, 8010);

            Socket listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
            // A Socket must be associated with an endpoint using the Bind method  
            listener.Bind(localEndPoint);
            // Specify how many requests a Socket can listen before it gives Server busy response.  
            // We will listen 10 requests at a time  
            listener.Listen(10);

            Socket forwarder = new Socket(SocketType.Stream, ProtocolType.Tcp);

            while (true)
            {
                Console.WriteLine("Connecting forwarder");
                forwarder.Connect(ipAddress, 9999);
                forwarder.Send(Encoding.ASCII.GetBytes("Connected\n"));
                
                Console.WriteLine("Connected");
                Console.WriteLine("Waiting for a connection...");
                Socket receiver = listener.Accept();

                //bool term = false;

                var sem = new SemaphoreSlim(2);
                
                var r = new Thread(() => {
                    
                    string data = string.Empty;
                    byte[] bytes = null;

                    while (receiver.Connected)
                    {
                        Console.WriteLine("Listening on the receiver");
                        bytes = new byte[1024];
                        
                        int bytesRec = receiver.Receive(bytes);
                        
                        lock(_lock) 
                        {
                            //if (term) {break;}
                            if (bytesRec > 0)
                            {
                                data += Encoding.ASCII.GetString(bytes, 0, bytesRec);
                                Console.WriteLine("r:" + data);
                                forwarder.Send(bytes);
                            }
                            else
                            {
                                Console.WriteLine("Receiver no bytes");
                                //term = true;
                                //break;
                            }
                        }
                    }

                    Console.WriteLine("Receiver disconnected");
                    sem.Release();
                });

                var s = new Thread(() => {
                    
                    string data = string.Empty;
                    byte[] bytes = null;

                    while (forwarder.Connected)
                    {
                        Console.WriteLine("listening on the forwarder");
                        bytes = new byte[1024];
                        
                        int bytesRec = forwarder.Receive(bytes);
                        
                        lock(_lock) 
                        {
                            //if (term) {break;}
                            if (bytesRec > 0)
                            {
                                data += Encoding.ASCII.GetString(bytes, 0, bytesRec);
                                Console.WriteLine("f:" + data);
                                if (receiver.Connected) { 
                                    receiver.Send(bytes);
                                }
                                else 
                                {
                                    forwarder.Shutdown(SocketShutdown.Both);
                                    break;
                                }
                            }
                            else
                            {
                                Console.WriteLine("Forwarder no bytes");
                                //term = true;
                                //break;
                            }
                        }
                    }
                    Console.WriteLine("Forwarder disconnected");
                    sem.Release();
                });

                Console.WriteLine("Starting threads");

                sem.Wait();
                sem.Wait();
                
                r.Start();
                s.Start();

                sem.Wait();
                Console.WriteLine("restarting");

                receiver.Close();
                forwarder.Close();

            }
            
            
        }
    }
}
