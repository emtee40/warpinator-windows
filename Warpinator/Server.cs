﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Common.Logging;
using Grpc.Core;
using Makaretu.Dns;

namespace Warpinator
{
    class Server
    {
        ILog log = new Common.Logging.Simple.ConsoleOutLogger("Server", LogLevel.All, true, false, true, "", true);
        const string SERVICE_TYPE = "_warpinator._tcp";

        public static Server current;

        public string DisplayName;
        public string UserName;
        public string Hostname;
        public ushort Port = 42000;
        public string UUID;
        public string ProfilePicture;
        public bool AllowOverwrite;
        public bool NotifyIncoming;
        public string DownloadDir;
        public bool Running = false;
        public string SelectedInterface = "{B9BF60D5-32E1-4BE1-A548-1CB105020611}"; //TODO: Make this a setting

        public Dictionary<string, Remote> Remotes = new Dictionary<string, Remote>();

        Grpc.Core.Server grpcServer;
        readonly ServiceDiscovery sd;
        readonly MulticastService mdns;
        ServiceProfile serviceProfile;
        readonly ConcurrentDictionary<string, ServiceRecord> mdnsServices = new ConcurrentDictionary<string, ServiceRecord>();

        public Server()
        {
            current = this;
            DisplayName = System.DirectoryServices.AccountManagement.UserPrincipal.Current.DisplayName;
            Hostname = Environment.MachineName;
            UserName = Environment.UserName;
            UUID = Hostname.ToUpper() + "-" + String.Format("{0:X6}", new Random().Next(0x1000000)); //TODO: Save this

            //Load settings...

            if (DownloadDir == null)
            {
                DownloadDir = Path.Combine(Utils.GetDefaultDownloadFolder(), "Warpinator");
                Directory.CreateDirectory(DownloadDir);
            }

            mdns = new MulticastService((ifaces) => ifaces.Where((iface) => SelectedInterface == null || iface.Id == SelectedInterface));
            mdns.UseIpv6 = false;
            sd = new ServiceDiscovery(mdns);

            sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
            sd.ServiceInstanceShutdown += OnServiceInstanceShutdown;
            mdns.AnswerReceived += OnAnswerReceived;
        }

        public void Start()
        {
            log.Info("-- Starting server");
            Running = true;
            StartGrpcServer(); //Also initializes authenticator for certserver
            CertServer.Start(Port);
            StartMDNS();
        }

        public async void Stop()
        {
            if (!Running)
                return;
            Running = false;
            sd.Unadvertise(serviceProfile);
            mdns.Stop();
            CertServer.Stop();
            await grpcServer.ShutdownAsync();
            log.Info("-- Server stopped");
        }

        private void StartGrpcServer()
        {
            KeyCertificatePair kcp = Authenticator.GetKeyCertificatePair();
            grpcServer = new Grpc.Core.Server() { 
                Services = { Warp.BindService(new GrpcService()) },
                Ports = { new ServerPort(Utils.GetLocalIPAddress(), Port, new SslServerCredentials(new List<KeyCertificatePair>() { kcp })) }
            };
            grpcServer.Start();
            log.Info("GRPC started");
        }

        private void StartMDNS(bool flush = false)
        {
            log.Debug("Starting mdns");
            
            foreach (var a in MulticastService.GetIPAddresses())
            {
                log.Debug($"IP address {a}");
            }
            mdns.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var nic in e.NetworkInterfaces)
                {
                    log.Debug($"discovered NIC '{nic.Name}', id: {nic.Id}");
                }
            };

            mdns.Start();
            sd.QueryServiceInstances(SERVICE_TYPE);

            serviceProfile = new ServiceProfile(UUID, SERVICE_TYPE, Port);
            serviceProfile.AddProperty("hostname", Utils.GetHostname());
            serviceProfile.AddProperty("type", flush ? "flush" : "real");
            sd.Advertise(serviceProfile);
            sd.Announce(serviceProfile);
        }

        private void OnServiceInstanceDiscovered(object sender, ServiceInstanceDiscoveryEventArgs e)
        {
            log.Debug($"Service discovered: '{e.ServiceInstanceName}'");
            if (!mdnsServices.ContainsKey(e.ServiceInstanceName.ToString()))
                mdnsServices.TryAdd(e.ServiceInstanceName.ToString(), new ServiceRecord() { FullName = e.ServiceInstanceName.ToString() });
        }
        
        private void OnServiceInstanceShutdown(object sender, ServiceInstanceShutdownEventArgs e)
        {
            log.Debug($"Service lost: '{e.ServiceInstanceName}'");
            if (Remotes.ContainsKey(e.ServiceInstanceName.ToString()))
            {
                var r = Remotes[e.ServiceInstanceName.ToString()];
                r.ServiceAvailable = false;
                r.UpdateUI();
            }
        }

        private void OnAnswerReceived(object sender, MessageEventArgs e)
        {
            log.Debug("-- Answer:");
            
            var servers = e.Message.Answers.OfType<SRVRecord>();
            foreach (var server in servers)
            {
                log.Debug($"  Service '{server.Name}' has hostname '{server.Target} and port {server.Port}'");
                if (!mdnsServices.ContainsKey(server.CanonicalName))
                    mdnsServices.TryAdd(server.CanonicalName, new ServiceRecord { FullName = server.CanonicalName });
                mdnsServices[server.CanonicalName].Hostname = server.Target.ToString();
                mdnsServices[server.CanonicalName].Port = server.Port;
            }

            var addresses = e.Message.Answers.OfType<AddressRecord>();
            foreach (var address in addresses)
            {
                if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    log.Debug($"  Hostname '{address.Name}' resolves to {address.Address}");
                    var svc = mdnsServices.Values.Where((s) => (s.Hostname == address.CanonicalName)).FirstOrDefault();
                    if (svc != null)
                        svc.Address = address.Address;
                }
            }

            var txts = e.Message.Answers.OfType<TXTRecord>();
            foreach (var txt in txts)
            {
                log.Debug("  Got strings: " + String.Join("; ", txt.Strings));
                mdnsServices[txt.CanonicalName].Txt = txt.Strings;
            }

            foreach (var svc in mdnsServices.Values)
            {
                if (!svc.resolved && svc.Address != null && svc.Txt != null)
                    OnServiceResolved(svc);
            }
        }

        private void OnServiceResolved(ServiceRecord svc)
        {
            svc.resolved = true; //TODO: support svc being updated
            string name = svc.FullName.Split('.')[0];
            log.Debug("Resolved " + name);
            if (name == UUID)
            {
                log.Debug("That's me - ignoring...");
                return;
            }

            var txt = new Dictionary<string, string>();
            svc.Txt.ForEach((t) => { var s = t.Split('='); txt.Add(s[0], s[1]); });
            // Ignore flush registration
            if (txt.ContainsKey("type") && txt["type"] == "flush")
            {
                log.Trace("Ignoring flush registration");
                return;
            }

            if (Remotes.ContainsKey(name))
            {
                Remote r = Remotes[name];
                log.Debug($"Service already known, status: {r.Status}");
                if (txt.ContainsKey("hostname"))
                    r.Hostname = txt["hostname"];
                r.ServiceAvailable = true;
                if (r.Status == Remote.RemoteStatus.DISCONNECTED || r.Status == Remote.RemoteStatus.ERROR)
                {
                    //TODO: Update and reconnect
                }
                else r.UpdateUI();
                return;
            }

            Remote remote = new Remote();
            remote.Address = svc.Address;
            if (txt.ContainsKey("hostname"))
                remote.Hostname = txt["hostname"];
            remote.Port = svc.Port;
            remote.ServiceName = name;
            remote.UUID = name;
            remote.ServiceAvailable = true;

            Remotes.Add(name, remote);
            Form1.UpdateUI();
            remote.Connect(); //TODO: Maybe new thread???
        }

        //TODO: Have a separaet hostname map (when that information comes first
        private class ServiceRecord
        {
            public string FullName;
            public string Hostname;
            public IPAddress Address;
            public int Port;
            public List<string> Txt;
            public bool resolved = false;
        }
    }
}