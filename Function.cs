using System.Collections.Generic;
using Amazon.Lambda.Core;
using System.Diagnostics;
using System;
using Amazon;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using System.Text;
using System.Net;
using System.Text.Json;
using System.Net.NetworkInformation;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.S3.Model;
using Amazon.S3;
using MimeKit;
using System.IO;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Web;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace TransferCaseViaEmail
{
    public class Function
    {
        private static readonly RegionEndpoint primaryRegion = RegionEndpoint.EUWest2;
        private static readonly RegionEndpoint bucketRegion = RegionEndpoint.EUWest1;
        private static readonly RegionEndpoint sqsRegion = RegionEndpoint.EUWest1;
        private static readonly String secretName = "nbcGlobal";
        private static readonly String secretAlias = "AWSCURRENT";

        private static String caseReference;
        private static String taskToken;
        private static String cxmEndPoint;
        private static String cxmAPIKey;
        private static String tableName = "MailBotCasesTest";
        private Secrets secrets;
        private CaseDetails caseDetails;


        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            if (await GetSecrets())
            {
                Boolean liveInstance = false;
                JObject inputJson = JObject.Parse(input.ToString());
                caseReference = (String)inputJson.SelectToken("CaseReference");
                taskToken = (String)inputJson.SelectToken("TaskToken");
                try
                {
                    if (context.InvokedFunctionArn.ToLower().Contains("prod"))
                    {
                        liveInstance = true;
                        tableName = "MailBotCasesLive";
                    }
                }
                catch (Exception)
                {
                }

                if (liveInstance)
                {
                    cxmEndPoint = secrets.cxmEndPointLive;
                    cxmAPIKey = secrets.cxmAPIKeyLive;
                }
                else
                {
                    cxmEndPoint = secrets.cxmEndPointTest;
                    cxmAPIKey = secrets.cxmAPIKeyTest;
                }
                caseDetails = await GetCustomerContactAsync();
                String emailID = await GetEmailIDAsync(caseReference, tableName);
                String emailContents = await FormatEmailAsync();
                await ForwardEmailAsync(caseDetails.emailTo, emailID);


                if (await SendMessageAsync(emailContents, caseDetails.emailTo,secrets.norbertSendFrom))
                {
                    await TransitionCaseAsync("close-case");
                    await SendSuccessAsync();
                }
            }
        }

        private async Task<Boolean> GetSecrets()
        {
            IAmazonSecretsManager client = new AmazonSecretsManagerClient(primaryRegion);

            GetSecretValueRequest request = new GetSecretValueRequest();
            request.SecretId = secretName;
            request.VersionStage = secretAlias;

            try
            {
                GetSecretValueResponse response = await client.GetSecretValueAsync(request);
                secrets = JsonConvert.DeserializeObject<Secrets>(response.SecretString);
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync("GetSecrets", error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.StackTrace);
                return false;
            }
        }

        private async Task<String> GetEmailIDAsync(String caseReference, String tableName)
        {
            try
            {
                AmazonDynamoDBClient dynamoDBClient = new AmazonDynamoDBClient(primaryRegion);
                Table caseTable = Table.LoadTable(dynamoDBClient, tableName);
                GetItemOperationConfig config = new GetItemOperationConfig
                {
                    AttributesToGet = new List<string> { "EmailID" },
                    ConsistentRead = true
                };
                Document document = await caseTable.GetItemAsync(caseReference, config);
                return document["EmailID"].AsPrimitive().Value.ToString();
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : GetContactFromDynamoAsync : " + error.Message);
                Console.WriteLine(error.StackTrace);
                return null;
            }
        }

        private async Task<String> FormatEmailAsync()
        {
            String emailBody = "";
            IAmazonS3 client = new AmazonS3Client(bucketRegion);
            try
            {
                GetObjectRequest objectRequest = new GetObjectRequest
                {
                    BucketName = "norbert.templates",
                    Key = "email-non-CXM-service.txt"
                };
                using (GetObjectResponse objectResponse = await client.GetObjectAsync(objectRequest))
                using (Stream responseStream = objectResponse.ResponseStream)
                using (StreamReader reader = new StreamReader(responseStream))
                {
                    emailBody = reader.ReadToEnd();
                }
                emailBody = emailBody.Replace("AAA", caseReference);
                emailBody = emailBody.Replace("KKK", HttpUtility.HtmlEncode(caseDetails.emailFrom));
                emailBody = emailBody.Replace("FFF", HttpUtility.HtmlEncode(caseDetails.enquiryDetails));
            }
            catch (Exception error)
            {
                await SendFailureAsync(" Reading Response Template", error.Message);
                Console.WriteLine("ERROR : FormatEmailAsync : Reading Response Template : " + error.Message);
                Console.WriteLine("ERROR : FormatEmailAsync : " + error.StackTrace);
            }
            return emailBody;
        }

        private async Task<Boolean> ForwardEmailAsync(String caseReference, String emailID)
        {
            IAmazonS3 client = new AmazonS3Client(bucketRegion);
            try
            {
                GetObjectRequest request = new GetObjectRequest
                {
                    BucketName = "norbert.emails",
                    Key = emailID
                };
                using (GetObjectResponse response = await client.GetObjectAsync(request))
                {
                    MimeMessage message = MimeMessage.Load(response.ResponseStream);

                    return true;
                }
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : An Unknown error encountered : {0}' when reading email", error.Message);
                Console.WriteLine(error.StackTrace);
                return false;
            }
        }

        private async Task<Boolean> SendMessageAsync(String emailBody, String emailTo, String emailFrom)
        {
            try
            {
                AmazonSQSClient amazonSQSClient = new AmazonSQSClient(sqsRegion);
                try
                {
                    SendMessageRequest sendMessageRequest = new SendMessageRequest();
                    sendMessageRequest.QueueUrl = secrets.sqsEmailURL;
                    sendMessageRequest.MessageBody = emailBody;
                    Dictionary<string, MessageAttributeValue> MessageAttributes = new Dictionary<string, MessageAttributeValue>();
                    MessageAttributeValue messageTypeAttribute1 = new MessageAttributeValue();
                    messageTypeAttribute1.DataType = "String";
                    messageTypeAttribute1.StringValue = "???";
                    MessageAttributes.Add("Name", messageTypeAttribute1);
                    MessageAttributeValue messageTypeAttribute2 = new MessageAttributeValue();
                    messageTypeAttribute2.DataType = "String";
                    messageTypeAttribute2.StringValue = emailTo;
                    MessageAttributes.Add("To", messageTypeAttribute2);
                    MessageAttributeValue messageTypeAttribute3 = new MessageAttributeValue();
                    messageTypeAttribute3.DataType = "String";
                    messageTypeAttribute3.StringValue = "Northampton Borough Council: Your Call Number is " + caseReference; ;
                    MessageAttributes.Add("Subject", messageTypeAttribute3);
                    MessageAttributeValue messageTypeAttribute4 = new MessageAttributeValue();
                    messageTypeAttribute4.DataType = "String";
                    messageTypeAttribute4.StringValue = emailFrom;
                    MessageAttributes.Add("From", messageTypeAttribute4);
                    sendMessageRequest.MessageAttributes = MessageAttributes;
                    SendMessageResponse sendMessageResponse = await amazonSQSClient.SendMessageAsync(sendMessageRequest);
                }
                catch (Exception error)
                {
                    await SendFailureAsync("Error sending SQS message", error.Message);
                    Console.WriteLine("ERROR : SendMessageAsync : Error sending SQS message : '{0}'", error.Message);
                    Console.WriteLine("ERROR : SendMessageAsync : " + error.StackTrace);
                    return false;
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync("Error starting AmazonSQSClient", error.Message);
                Console.WriteLine("ERROR : SendMessageAsync :  Error starting AmazonSQSClient : '{0}'", error.Message);
                Console.WriteLine("ERROR : SendMessageAsync : " + error.StackTrace);
                return false;
            }
            return true;
        }

        private async Task<CaseDetails> GetCustomerContactAsync()
        {
            CaseDetails caseDetails = new CaseDetails();
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            String requestParameters = "key=" + cxmAPIKey;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/service-api/norbert/case/" + caseReference + "?" + requestParameters);
            try
            {
                HttpResponseMessage response = cxmClient.SendAsync(request).Result;
                if (response.IsSuccessStatusCode)
                {
                    HttpContent responseContent = response.Content;
                    String responseString = responseContent.ReadAsStringAsync().Result;
                    JObject caseSearch = JObject.Parse(responseString);
                    caseDetails.emailTo = (String)caseSearch.SelectToken("values.forward_email_to");
                    caseDetails.emailFrom = (String)caseSearch.SelectToken("values.email");
                    caseDetails.enquiryDetails = (String)caseSearch.SelectToken("values.enquiry_details");
                }
                else
                {
                    await SendFailureAsync("Getting case details for " + caseReference, response.StatusCode.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + request.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + response.StatusCode.ToString());
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync("Getting case details for " + caseReference, error.Message);
                Console.WriteLine("ERROR : GetStaffResponseAsync : " + error.StackTrace);
            }
            return caseDetails;
        }

        private async Task<Boolean> TransitionCaseAsync(String transitionTo)
        {
            Boolean success = false;
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            string requestParameters = "key=" + cxmAPIKey;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/service-api/norbert/case/" + caseReference + "/transition/" + transitionTo + "?" + requestParameters);
            HttpResponseMessage response = cxmClient.SendAsync(request).Result;
            if (response.IsSuccessStatusCode)
            {
                success = true;
            }
            else
            {
                await SendFailureAsync("CXM Failed to transiton : " + caseReference + " to " + transitionTo, "TransitionCaseAsync");
                Console.WriteLine("ERROR CXM Failed to transiton : " + caseReference + " to " + transitionTo);
            }
            return success;
        }

        private async Task SendSuccessAsync()
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Success"  },
                { "Message" , "Completed"}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.Message);
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        private async Task SendFailureAsync(String failureCause, String failureError)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskFailureRequest failureRequest = new SendTaskFailureRequest();
            failureRequest.Cause = failureCause;
            failureRequest.Error = failureError;
            failureRequest.TaskToken = taskToken;

            try
            {
                await client.SendTaskFailureAsync(failureRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendFailureAsync : " + error.Message);
                Console.WriteLine("ERROR : SendFailureAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

    }
    class CaseDetails
    {
        public String emailTo { get; set; } = "";
        public String emailFrom { get; set; } = "";
        public String enquiryDetails { get; set; } = "";
    }

    public class Sentiment
    {
        public Boolean success { get; set; }
        public String sentimentRating { get; set; }
        public String sentimentMixed { get; set; }
        public String sentimentNegative { get; set; }
        public String sentimentNeutral { get; set; }
        public String sentimentPositive { get; set; }
    }

    public class Secrets
    {
        public String cxmEndPointTest { get; set; }
        public String cxmEndPointLive { get; set; }
        public String cxmAPIKeyTest { get; set; }
        public String cxmAPIKeyLive { get; set; }
        public String sqsEmailURL { get; set; }
        public String norbertSendFrom { get; set; }
    }
}