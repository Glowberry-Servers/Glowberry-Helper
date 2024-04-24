using glowberry.common;
using glowberry.utils;

namespace glowberry.helper.workers.server;

/// <summary>
/// This class is responsible for handling the firewall rules through the provided DLL api for it.
/// </summary>
public class FirewallHandler
{
    
    /// <summary>
    /// Checks if the handle firewall option is turned on. Depending on the state, either removes
    /// or adds the inbound and outbound port rules for the server. 
    /// </summary>
    /// <param name="editor">The server editor to use to check the firewall settings</param>
    public static  void AutoHandleFirewall(ServerEditor editor)
    {
        string lookupPrefix = $"Glowberry-{editor.GetServerName()}";
        
        // If the user has the handle firewall option on, check if the rules are updated
        if (editor.GetServerInformation().HandleFirewall)
        {
            EnableFirewallRules(editor, lookupPrefix);
            return;
        }

        FirewallUtils.RemovePortRuleByName(lookupPrefix);
    }
    
    /// <summary>
    /// Checks if the firewall rules set for this server are using the proper port
    /// </summary>
    /// <param name="editor">The server editor to use to check the firewall settings</param>
    /// <param name="lookupPrefix">The prefix of the rule name to look for</param>
    private static void EnableFirewallRules(ServerEditor editor, string lookupPrefix)
    {
        // If the port rule is present and correct, return
        int port = editor.GetServerInformation().Port;
        if (FirewallUtils.IsPortRulePresent(lookupPrefix, port)) return;
        
        // If there is a port rule present but with a different port, remove it
        if (FirewallUtils.IsPortRulePresent(lookupPrefix)) 
            FirewallUtils.RemovePortRuleByName(lookupPrefix);
        
        FirewallUtils.CreatePortRuleForServer(editor);
    }
    
}