using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles Actions API WebDriver commands.
    /// Endpoints: Perform complex input actions, release actions
    /// </summary>
    public class ActionCommands : IWebDriverCommand
    {
        public bool CanHandle(string method, string path)
        {
            if (!path.Contains("/session/")) return false;

            // POST /session/{id}/actions - Perform actions
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/actions/?$")) return true;
            
            // DELETE /session/{id}/actions - Release actions
            if (method == "DELETE" && Regex.IsMatch(path, @"/session/[^/]+/actions/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            if (context.Session == null) return WebDriverResponse.InvalidSession();

            var path = context.Path.TrimEnd('/');

            // POST /session/{id}/actions - Perform actions
            if (context.Method == "POST" && path.EndsWith("/actions"))
            {
                if (!context.Body.TryGetProperty("actions", out var actionsProp))
                    return WebDriverResponse.Error400("Missing 'actions' parameter");

                var actionChains = new List<ActionChain>();

                foreach (var chain in actionsProp.EnumerateArray())
                {
                    var actionChain = new ActionChain();
                    
                    if (chain.TryGetProperty("type", out var typeProp))
                        actionChain.Type = typeProp.GetString();
                    if (chain.TryGetProperty("id", out var idProp))
                        actionChain.Id = idProp.GetString();

                    if (chain.TryGetProperty("actions", out var subActions))
                    {
                        foreach (var action in subActions.EnumerateArray())
                        {
                            var act = new InputAction();
                            
                            if (action.TryGetProperty("type", out var actTypeProp))
                                act.Type = actTypeProp.GetString();
                            if (action.TryGetProperty("duration", out var durationProp))
                                act.Duration = durationProp.GetInt32();
                            if (action.TryGetProperty("x", out var xProp))
                                act.X = xProp.GetInt32();
                            if (action.TryGetProperty("y", out var yProp))
                                act.Y = yProp.GetInt32();
                            if (action.TryGetProperty("button", out var buttonProp))
                                act.Button = buttonProp.GetInt32();
                            if (action.TryGetProperty("value", out var valueProp))
                                act.Value = valueProp.GetString();
                            if (action.TryGetProperty("origin", out var originProp))
                            {
                                if (originProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                    act.Origin = originProp.GetString();
                                else if (originProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (originProp.TryGetProperty("element-6066-11e4-a52e-4f735466cecf", out var elemId))
                                        act.Origin = elemId.GetString();
                                }
                            }

                            actionChain.Actions.Add(act);
                        }
                    }

                    actionChains.Add(actionChain);
                }

                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.PerformActionsAsync(actionChains));
                
                return WebDriverResponse.Success(null);
            }

            // DELETE /session/{id}/actions - Release actions
            if (context.Method == "DELETE" && path.EndsWith("/actions"))
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.ReleaseActionsAsync());
                return WebDriverResponse.Success(null);
            }

            return WebDriverResponse.Error404("Action command not found");
        }
    }
    // ActionChain and InputAction are defined in FenBrowser.FenEngine.Rendering.BrowserApi
}

