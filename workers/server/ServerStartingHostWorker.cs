using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using glowberry.api.server;
using glowberry.common;
using glowberry.common.caches;
using glowberry.common.factories;
using glowberry.common.handlers;
using glowberry.common.models;
using glowberry.common.server.starters;
using LaminariaCore_General.common;
using LaminariaCore_General.utils;
using Open.Nat;
using static glowberry.common.configuration.Constants;

namespace glowberry.helper.workers.server
{
    /// <summary>
    /// This class is responsible for providing a NamedPipe server that allows processes to communicate directly
    /// with the minecraft server. <br/>
    ///
    /// This is needed because the redirected output stream is not able to be read from if we get the process
    /// externally, so the usage of sockets is necessary to setup a server that can directly communicate
    /// with the initialised process.
    /// </summary>
    internal class ServerStartingHostWorker
    {
        
        /// <summary>
        /// The minecraft server process to interact with.
        /// </summary>
        private Process MinecraftServer { get; set; }

        /// <summary>
        /// The server editor associated with the minecraft server to run
        /// </summary>
        private ServerEditor Editor { get; }
        
        /// <summary>
        /// The server interactions API to use to interact with the server
        /// </summary>
        private ServerInteractions InteractionsAPI { get; }
        
        /// <summary>
        /// The server section to use with the work
        /// </summary>
        private Section ServerSection { get; }


        /// <summary>
        /// Main constructor for the ServerStartingHostWorker class. 
        /// </summary>
        /// <param name="serverName">The server name associated to this instance of the worker</param>
        public ServerStartingHostWorker(string serverName)
        {
            this.ServerSection = FileSystem.GetFirstSectionNamed("servers").GetFirstSectionNamed(serverName);
            ServerEditor editor = GlobalEditorsCache.INSTANCE.GetOrCreate(this.ServerSection);
            this.InteractionsAPI = new ServerAPI().Interactions(editor.ServerSection.SimpleName);
            this.Editor = editor;
        }
        
        /// <summary>
        /// Checks if the current wifi network supports UPnP, and if so, creates a port mapping for the
        /// specified ports.
        /// If the port mapping already exists, the current one will be ignored.
        /// </summary>
        /// <param name="internalPort">The internal port to redirect incoming traffic to</param>
        /// <param name="externalPort">The external port to use to redirect the traffic</param>
        /// <returns>Either true or false, depending on whether the port mapping was successful or not</returns>
        private static bool TryCreatePortMapping(int internalPort, int externalPort)
        {
            try
            {
                // Discover the router, on a 10 second timeout.
                NatDiscoverer discoverer = new();
                NatDevice device = discoverer.DiscoverDeviceAsync(PortMapper.Upnp, new CancellationTokenSource(5000))
                    .Result;

                // Create a new TCP port mapping in the router identified by the external port.
                Logging.Logger.Info(@$"Creating a new TCP port mapping for I{internalPort}@E{externalPort}..."); 
                device.CreatePortMapAsync(new Mapping(Protocol.Tcp, internalPort, externalPort,
                        $"TCP-Glowberry@{internalPort}")).Wait();

                return true;
            }

            // If the network does not support UPnP, ignore it.
            catch (NatDeviceNotFoundException)
            {
                Logging.Logger.Warn(@"The current network does not support UPnP or the router wasn't found. Ignoring...");
            }

            // If the port mapping already exists, ignore it.
            catch (AggregateException e) when (e.InnerException is MappingException)
            {
                Logging.Logger.Warn(@$"The I{internalPort}@E{externalPort} TCP port mapping already exists. Ignoring...");
                return true;
            }

            // If any other exception occurs, log it and return false.
            catch (Exception e)
            {
                Logging.Logger.Error(@$"An error occured while trying to create the port mapping.\n{e.StackTrace}");
            }

            return false;
        }
        
        /// <summary>
        /// Tries to remove the port mapping for the specified ports.
        /// </summary>
        /// <param name="internalPort">The internal port to redirect incoming traffic to</param>
        /// <param name="externalPort">The external port to use to redirect the traffic</param>
        /// <returns>Either true or false, depending on whether the removal of the port mapping was successful or not</returns>
        private static bool TryRemovePortMapping(int internalPort, int externalPort)
        {
            try
            {
                // Discover the router, on a 10 second timeout.
                NatDiscoverer discoverer = new();
                NatDevice device = discoverer.DiscoverDeviceAsync(PortMapper.Upnp, new CancellationTokenSource(5000))
                    .Result;

                // Create a new TCP port mapping in the router identified by the external port.
                device.DeletePortMapAsync(new Mapping(Protocol.Tcp, internalPort, externalPort)).Wait();
                Logging.Logger.Info(@$"Removed the TCP port mapping for I{internalPort}@E{externalPort}..."); 

                return true;
            }

            // If the network does not support UPnP, ignore it.
            catch (NatDeviceNotFoundException)
            {
                Logging.Logger.Warn(@"The current network does not support UPnP or the router wasn't found. Ignoring...");
            }

            // If the port mapping already exists, ignore it.
            catch (AggregateException e) when (e.InnerException is MappingException)
            {
                Logging.Logger.Warn(@$"The I{internalPort}@E{externalPort} TCP port mapping does not exist. Ignoring...");
                return true;
            }

            // If any other exception occurs, log it and return false.
            catch (Exception e)
            {
                Logging.Logger.Error(@$"An error occured while trying to remove the port mapping.\n{e.StackTrace}");
            }

            return false;
        }
        
        /// <summary>
        /// Wraps the internal startup code in such a way that any exceptions are caught and logged.
        /// </summary>
        /// <param name="outputSystem"></param>
        public void Start(MessageProcessingOutputHandler outputSystem)
        {
            try { this.InternalStart(outputSystem); }
            
            // If the pipes crash, exit the program after logging
            catch (IdentityNotMappedException e)
            {
                Logging.Logger.Error(e);
                Environment.Exit(1);
            }
            
            catch (Exception e) { Logging.Logger.Error(e); }
        }
        
        /// <summary>
        /// Starts the server and the infinite cycle of listening to messages from the server
        /// until the process stops.
        /// </summary>
        /// <param name="outputSystem">The output system to use in order to log the messages</param>
        private void InternalStart(MessageProcessingOutputHandler outputSystem)
        {
            // Gets the server starter to be used to run the server
            string serverType = this.Editor.GetServerInformation().Type;
            AbstractServerStarter serverStarter = new ServerTypeMappingsFactory()
                .GetStarterFor(serverType, outputSystem);
            
            // Gets an available port starting on the one specified, automatically update and flush the buffers.
            if (this.Editor.HandlePortForServer() == 1)
            {
                string errorMessage = Logging.Logger.Error("Could not find a port to start the server with. Please change the port in the server properties or free up ports to use.");
                outputSystem.Write(errorMessage, Color.Firebrick);
                return;
            }
            
            // If the server has the Handle Firewall mode enabled, automatically handle the firewall configs
            FirewallHandler.AutoHandleFirewall(this.Editor);

            /*
             Tries to create the port mapping for the server, and updates the server_settings.xml
             file with the correct ip based on the success of the operation.
             This will inevitably fail if the router does not support UPnP.
            */
            ServerInformation info = this.Editor.GetServerInformation();

            // If the user has UPnP on, try to create the port mapping
            if (info.UPnPOn && TryCreatePortMapping(info.Port, info.Port))
                info.IPAddress = NetworkUtils.GetExternalIPAddress();
             
            else info.IPAddress = NetworkUtils.GetLocalIPAddress();
            
            // If the user does not have upnp on, remove the port mapping
            if (!info.UPnPOn) Task.Run( () => TryRemovePortMapping(info.Port, info.Port));
            
            // Actually runs the minecraft server
            this.MinecraftServer = serverStarter.Run(this.Editor);
            Logging.Logger.Info($"Running {this.ServerSection.SimpleName} on {info.IPAddress}");

            // Starts up the pipe server to listen to messages from the client
            PipeSecurity pipeRules = new PipeSecurity();
            SecurityIdentifier id = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            
            pipeRules.AddAccessRule(new PipeAccessRule("SYSTEM", PipeAccessRights.FullControl, AccessControlType.Allow));
            pipeRules.AddAccessRule(new PipeAccessRule(id, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            
            using NamedPipeServerStream pipeServer = new("piped" + this.Editor.ServerSection.SimpleName, PipeDirection.In,
                1, PipeTransmissionMode.Message, PipeOptions.WriteThrough | PipeOptions.Asynchronous, 1024, 1024, pipeRules);

            // Starts listening for a connection to the pipe server
            pipeServer.BeginWaitForConnection(HandleConnectionCallback, pipeServer);
            Logging.Logger.Info("Initialised pipe connection for server " + Editor.ServerSection.SimpleName, LoggingType.File);
            info.CurrentServerProcessID = this.MinecraftServer.Id;
            
            // Updates the buffers so as to get everything ready for the flag
            this.Editor.UpdateBuffers(info.ToDictionary());
            this.Editor.FlushBuffers();
            
            // Creates and deletes a ".flag.started" that should be tracked by a SystemFileWatcher so that a program
            // can know when the server was started
            this.ServerSection.AddDocument(".flag.started");
            this.ServerSection.RemoveDocument(".flag.started");
            
            // Runs the server until the process it is contained in exits.
            this.MinecraftServer.WaitForExit();
            InteractionsAPI.ClearOutputBuffer();
            pipeServer.Close();
        }

        /// <summary>
        /// Handles the callback for the pipe server, which is called whenever a message is sent through the pipe.
        /// This should be used within the asynchronous method BeginWaitForConnection. 
        /// </summary>
        /// <param name="data">The async result data containing the server and the information</param>
        private async void HandleConnectionCallback(IAsyncResult data)
        {
            NamedPipeServerStream pipeServer = (NamedPipeServerStream) data.AsyncState;

            try
            {
                // Stops waiting for a connection, and closes the pipe if the server is not connected.
                pipeServer.EndWaitForConnection(data);

                // Gets the next message sent through the pipe
                string message = await this.GetNextPipedMessage(pipeServer);
                Logging.Logger.Info("Received message from pipe: " + message, LoggingType.File);

                // If the message is empty, return, otherwise write it to the server's stdin
                if (pipeServer.IsConnected) pipeServer.Disconnect();
                if (string.IsNullOrEmpty(message)) return;
                this.WriteToServerStdin(message);
            }

            // If an IOException is thrown, log it and restart the pipe server.
            catch (IOException e)
            {
                Logging.Logger.Error(e);
            }

            // If an ObjectDisposedException is thrown, log it and restart the pipe server.
            catch (ObjectDisposedException e)
            {
                Logging.Logger.Error(e);
            }

            // If an OutOfMemoryException is thrown, log it and restart the pipe server.
            catch (OutOfMemoryException e)
            {
                Logging.Logger.Error(e);
            }

            // Restarts the pipe server.
            try { pipeServer.BeginWaitForConnection(HandleConnectionCallback, pipeServer); }
            catch (IOException) {}
            catch (ObjectDisposedException) {}
        }

        /// <summary>
        /// Waits for the message the connection is sending through, returning it in the form of a string.
        /// </summary>
        /// <param name="server">The server to use to receive the messages</param>
        private async Task<string> GetNextPipedMessage(NamedPipeServerStream server)
        {
            using StreamReader reader = new StreamReader(server, Encoding.UTF8, true, 1024, true);
            return await reader.ReadLineAsync();
        }
        
        /// <summary>
        /// Writes a message into the minecraft server's stdin.
        /// </summary>
        /// <param name="message">The message to be written</param>
        private async void WriteToServerStdin(string message)
        {
            try
            {
                await this.MinecraftServer.StandardInput.WriteLineAsync(message);;
                Logging.Logger.Info("Sent message to server: <<" + message + ">>", LoggingType.File);
            }
            
            catch (Exception e) { Logging.Logger.Error(e); }
        }

    }
}