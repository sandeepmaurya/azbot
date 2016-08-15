using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Microsoft.Bot.Connector;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;

namespace Azbot
{
    [BotAuthentication]
    public class MessagesController : ApiController
    {
        /// <summary>
        /// POST: api/Messages
        /// Receive a message from a user and reply to it
        /// </summary>
        public async Task<HttpResponseMessage> Post([FromBody]Activity activity)
        {
            if (activity.Type == ActivityTypes.Message)
            {
                string botResponse = null;
                ConnectorClient connector = new ConnectorClient(new Uri(activity.ServiceUrl));

                StateClient stateClient = activity.GetStateClient();
                BotData state = stateClient.BotState.GetUserData(activity.ChannelId, activity.Conversation.Id);

                // Check if there is any active question.
                var activeQuestion = state.GetProperty<string>("ActiveQuestion");
                if (!string.IsNullOrWhiteSpace(activeQuestion))
                {
                    botResponse = await ProcessQuestionResponse(state, activeQuestion, activity.Text);
                }
                else
                {
                    // No active question. Figure out the intent of conversation.
                    botResponse = await ProcessIntent(state, activity.Text);
                }

                Activity reply = activity.CreateReply(botResponse);
                await connector.Conversations.ReplyToActivityAsync(reply);
            }
            else
            {
                HandleSystemMessage(activity);
            }

            var response = Request.CreateResponse(HttpStatusCode.OK);
            return response;
        }

        private async Task<string> ProcessIntent(BotData state, string activityText)
        {
            LuisResponse luisResponse = await LuisClient.ParseUserInput(activityText);
            switch (luisResponse.intents[0].intent)
            {
                case "Greet":
                    return "Hi there. How can I help you today?";
                case "ListSubscriptions":
                    var servicePrincipalCredentials = state.GetProperty<Tuple<string, string, string>>("ServicePrincipalCredentials");
                    if (servicePrincipalCredentials != null)
                    {
                        return await PrepareSubscriptionsResponse(servicePrincipalCredentials);
                    }
                    else
                    {
                        state.SetProperty<string>("ActiveQuestion", "ServicePrincipalCredentials");
                        return "Sure. Please enter your AD application client id, service principal password and tenant id separated by commas.";
                    }
                default:
                    break;
            }

            return "I'm sorry. I did not understand you.";
        }

        private async Task<string> ProcessQuestionResponse(BotData state, string activeQuestion, string activityText)
        {
            switch (activeQuestion)
            {
                case "ServicePrincipalCredentials":
                    try
                    {
                        var credentials = ExtractCredentials(activityText);
                        var response = await PrepareSubscriptionsResponse(credentials);
                        state.SetProperty<string>("ActiveQuestion", null);
                        state.SetProperty<Tuple<string, string, string>>("ServicePrincipalCredentials", credentials);
                        return response;
                    }
                    catch
                    {
                        return "Please enter your AD application client id, service principal password and tenant id separated by commas.";
                    }
            }

            return "I'm sorry. I did not understand you.";
        }

        private Tuple<string, string, string> ExtractCredentials(string activityText)
        {
            var subStrings = activityText.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (subStrings.Length == 3)
            {
                return new Tuple<string, string, string>(subStrings[0], subStrings[1], subStrings[2]);
            }

            return null;
        }

        private async Task<string> PrepareSubscriptionsResponse(Tuple<string, string, string> creds)
        {
            var authToken = await GetAuthorizationToken(creds.Item1, creds.Item2, creds.Item3);
            TokenCredentials tokenCreds = new TokenCredentials(authToken);

            SubscriptionClient subscriptionClient = new SubscriptionClient(tokenCreds);

            StringBuilder builder = new StringBuilder();
            foreach (Subscription sub in await subscriptionClient.Subscriptions.ListAsync())
            {
                builder.AppendLine(string.Format("SubscriptionId: {0}, DisplayName:{1}", sub.SubscriptionId, sub.DisplayName));
            }

            return builder.ToString();
        }

        private static async Task<string> GetAuthorizationToken(string clientId, string servicePrincipalPassword, string tenantId)
        {
            ClientCredential cc = new ClientCredential(clientId, servicePrincipalPassword);
            var context = new AuthenticationContext("https://login.windows.net/" + tenantId);
            var result = await context.AcquireTokenAsync("https://management.azure.com/", cc);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to obtain the JWT token");
            }

            return result.AccessToken;
        }

        private Activity HandleSystemMessage(Activity message)
        {
            if (message.Type == ActivityTypes.DeleteUserData)
            {
                // Implement user deletion here
                // If we handle user deletion, return a real message
            }
            else if (message.Type == ActivityTypes.ConversationUpdate)
            {
                // Handle conversation state changes, like members being added and removed
                // Use Activity.MembersAdded and Activity.MembersRemoved and Activity.Action for info
                // Not available in all channels
            }
            else if (message.Type == ActivityTypes.ContactRelationUpdate)
            {
                // Handle add/remove from contact lists
                // Activity.From + Activity.Action represent what happened
            }
            else if (message.Type == ActivityTypes.Typing)
            {
                // Handle knowing tha the user is typing
            }
            else if (message.Type == ActivityTypes.Ping)
            {
            }

            return null;
        }
    }
}