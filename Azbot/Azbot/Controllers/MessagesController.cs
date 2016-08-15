using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using Microsoft.Bot.Connector;
using Newtonsoft.Json.Linq;

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
            ConnectorClient connector = new ConnectorClient(new Uri(activity.ServiceUrl));
            StateClient stateClient = activity.GetStateClient();

            if (activity.Type == ActivityTypes.Message)
            {
                string botResponse = null;
                BotData state = stateClient.BotState.GetPrivateConversationData(activity.ChannelId, activity.Conversation.Id, activity.From.Id);

                // Check if there is any active question.
                var activeQuestion = state.GetProperty<string>("ActiveQuestion");
                if (!string.IsNullOrWhiteSpace(activeQuestion))
                {
                    botResponse = await ProcessQuestionResponse(stateClient, state, activeQuestion, activity);
                }
                else
                {
                    // No active question. Figure out the intent of conversation.
                    botResponse = await ProcessIntent(stateClient, state, activity);
                }

                Activity reply = activity.CreateReply(botResponse);
                await connector.Conversations.ReplyToActivityAsync(reply);
            }
            else
            {
                HandleSystemMessage(stateClient, activity);
            }

            var response = Request.CreateResponse(HttpStatusCode.OK);
            return response;
        }

        private async Task<string> ProcessIntent(StateClient stateClient, BotData state, Activity activity)
        {
            LuisResponse luisResponse = await LuisClient.ParseUserInput(activity.Text);
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
                        stateClient.BotState.SetPrivateConversationData(activity.ChannelId, activity.Conversation.Id, activity.From.Id, state);
                        return "Sure. Please enter your AD application client id, service principal password and tenant id separated by commas.";
                    }
                case "Thanks":
                    return "My pleasure.";
                case "DefaultSubscription":
                    var subscriptionId = luisResponse.entities[0].entity.Replace(" ", "");
                    state.SetProperty<string>("DefaultSubscription", subscriptionId);
                    stateClient.BotState.SetPrivateConversationData(activity.ChannelId, activity.Conversation.Id, activity.From.Id, state);
                    return "Subscription [" + subscriptionId + "] is set as the default subscription.";
                case "ListResourceGroups":
                    var defaultSubscription = state.GetProperty<string>("DefaultSubscription");
                    var credentials = state.GetProperty<Tuple<string, string, string>>("ServicePrincipalCredentials");
                    if (string.IsNullOrWhiteSpace(defaultSubscription) || credentials == null)
                    {
                        return "Please set a default subscription.";
                    }
                    return await PrepareResourceGroupsResponse(credentials, defaultSubscription);
                default:
                    break;
            }

            return "I'm sorry. I did not understand you.";
        }

        private async Task<string> ProcessQuestionResponse(StateClient stateClient, BotData state, string activeQuestion, Activity activity)
        {
            switch (activeQuestion)
            {
                case "ServicePrincipalCredentials":
                    try
                    {
                        var credentials = ExtractCredentials(activity.Text);
                        var response = await PrepareSubscriptionsResponse(credentials);
                        state.SetProperty<string>("ActiveQuestion", string.Empty);
                        state.SetProperty<Tuple<string, string, string>>("ServicePrincipalCredentials", credentials);
                        stateClient.BotState.SetPrivateConversationData(activity.ChannelId, activity.Conversation.Id, activity.From.Id, state);
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
            string requestURL = String.Format("https://smarm.azurewebsites.net/api/arm/GetSubscriptions?clientId={0}&clientSecret={1}&tenantId={2}",
                                HttpUtility.UrlEncode(creds.Item1),
                                HttpUtility.UrlEncode(creds.Item2),
                                HttpUtility.UrlEncode(creds.Item3));

            JObject responseDoc = await GetResponse(requestURL);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Your subscriptions:   ");

            foreach (var item in responseDoc["value"] as JArray)
            {
                JObject subDoc = item as JObject;
                builder.AppendLine(string.Format("SubscriptionId: {0}, DisplayName:{1}", subDoc["subscriptionId"], subDoc["displayName"]));
            }

            return builder.ToString();
        }

        private async Task<string> PrepareResourceGroupsResponse(Tuple<string, string, string> creds, string subscriptionId)
        {
            string requestURL = String.Format("https://smarm.azurewebsites.net/api/arm/GetResourceGroups?clientId={0}&clientSecret={1}&tenantId={2}&subscriptionId={3}",
                                HttpUtility.UrlEncode(creds.Item1),
                                HttpUtility.UrlEncode(creds.Item2),
                                HttpUtility.UrlEncode(creds.Item3),
                                HttpUtility.UrlEncode(subscriptionId));

            JObject responseDoc = await GetResponse(requestURL);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Your resource groups:    ");

            foreach (var item in responseDoc["value"] as JArray)
            {
                JObject subDoc = item as JObject;
                builder.AppendLine(string.Format("Name: {0}", subDoc["name"]));
            }

            return builder.ToString();
        }

        private static async Task<JObject> GetResponse(string requestURL)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestURL);
            request.Headers.Add(HttpRequestHeader.Authorization, "Basic YXBpOnNlY3JldDEyMyFAIw==");
            request.ContentType = "application/json";

            string responseString = null;
            using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
            {
                Stream responseStream = response.GetResponseStream();
                using (StreamReader rdr = new StreamReader(responseStream, Encoding.UTF8))
                {
                    responseString = rdr.ReadToEnd();
                }
            }

            JObject responseDoc = JObject.Parse(responseString);
            return responseDoc;
        }

        private Activity HandleSystemMessage(StateClient stateClient, Activity activity)
        {
            if (activity.Type == ActivityTypes.DeleteUserData)
            {
                // Implement user deletion here
                // If we handle user deletion, return a real message
                stateClient.BotState.DeleteStateForUser(activity.ChannelId, activity.From.Id);
            }
            else if (activity.Type == ActivityTypes.ConversationUpdate)
            {
                // Handle conversation state changes, like members being added and removed
                // Use Activity.MembersAdded and Activity.MembersRemoved and Activity.Action for info
                // Not available in all channels
            }
            else if (activity.Type == ActivityTypes.ContactRelationUpdate)
            {
                // Handle add/remove from contact lists
                // Activity.From + Activity.Action represent what happened
                stateClient.BotState.DeleteStateForUser(activity.ChannelId, activity.From.Id);
            }
            else if (activity.Type == ActivityTypes.Typing)
            {
                // Handle knowing tha the user is typing
            }
            else if (activity.Type == ActivityTypes.Ping)
            {
            }

            return null;
        }
    }
}