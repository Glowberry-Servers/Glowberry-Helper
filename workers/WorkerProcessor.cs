using glowberry.common.handlers;
using glowberry.console;

namespace glowberry.helper.workers
{
    /// <summary>
    /// This class is responsible for delegating the requested work to the appropriate
    /// worker method, through the commands and parameters given.
    /// </summary>
    ///
    /// The methods created in here must use the provided API to interact with the backend, and their signature
    /// must be «public void Command_(Command Name) (ConsoleCommand command)».
    public class WorkerProcessor : AbstractConsoleCommandExecutor
    {
        /// <summary>
        /// Runs a server with the given parameters.
        /// </summary>
        private void Command_Run_Server(ConsoleCommand command)
        {
            string serverName = command.GetValueForField("name");
            if (serverName == null) return;

            new ServerStartingHostWorker(serverName).Start(new MessageProcessingOutputHandler(null));
        }
    }
}