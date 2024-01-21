using glowberry.console;
using glowberry.helper.workers;

namespace glowberry.helper
{
    
    /// <summary>
    /// This helper program is responsible for running any predefined work with variable inputs
    /// given through the command line in its process. <br/>
    ///
    /// Any commands sent through here are assumed to be valid, and will not be checked for validity. Not
    /// only that, no input should also be taken out of the helper process directly, as this is meant
    /// to be a fire-and-forget process.
    /// </summary>
    internal static class Program
    {

        /// <summary>
        /// Main entry point for the application. Takes in the command line arguments
        /// </summary>
        public static void Main(string[] args)
        {
            if (args.Length == 0) return;
            
            ConsoleCommand command = ConsoleCommandParser.Parse(args);
            new WorkerProcessor().ExecuteCommand(command);
        }
    }
}