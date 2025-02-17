﻿// <copyright file="QnAPairServiceFacade.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Microsoft.Teams.Apps.FAQPlusPlus.Common.Components
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using global::Azure.Search.Documents.Models;
    using global::Azure.AI.Language.QuestionAnswering;
    using global::Azure.Search.Documents;

    using Microsoft.Bot.Builder;
    using Microsoft.Bot.Schema;
    using Microsoft.Bot.Schema.Teams;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.Cards;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.Helpers;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.Models;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.Models.Configuration;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.Properties;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.Providers;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.TeamsActivity;
    using Newtonsoft.Json.Linq;
    using System.IO;
    using Newtonsoft.Json;
    using global::Azure.AI.OpenAI;

    /// <summary>
    /// Class that handles get/add/update of QnA pairs.
    /// </summary>
    public class QnAPairServiceFacade : IQnAPairServiceFacade
    {
        private readonly IConfigurationDataProvider configurationProvider;
        private readonly IActivityStorageProvider activityStorageProvider;
        private readonly IQuestionAnswerServiceProvider questionAnswerServiceProvider;
        private readonly ILogger<QnAPairServiceFacade> logger;
        private readonly string appBaseUri;
        private readonly BotSettings options;

        private SearchClient srchclient;
        private OpenAIClient openAIClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="QnAPairServiceFacade"/> class.
        /// </summary>
        /// <param name="configurationProvider">Configuration Provider.</param>
        /// <param name="activityStorageProvider">Activity storage provider.</param>
        /// <param name="questionAnswerServiceProvider">Question answer service provider.</param>
        /// <param name="botSettings">Represents a set of key/value application configuration properties for FaqPlusPlus bot.</param>ram>
        /// <param name="logger">Instance to send logs to the Application Insights service.</param>
        public QnAPairServiceFacade(
            Common.Providers.IConfigurationDataProvider configurationProvider,
            IQuestionAnswerServiceProvider questionAnswerServiceProvider,
            IActivityStorageProvider activityStorageProvider,
            IOptionsMonitor<BotSettings> botSettings,
            ILogger<QnAPairServiceFacade> logger)
        {
            this.configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            this.questionAnswerServiceProvider = questionAnswerServiceProvider ?? throw new ArgumentNullException(nameof(questionAnswerServiceProvider));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.activityStorageProvider = activityStorageProvider ?? throw new ArgumentNullException(nameof(activityStorageProvider));
            if (botSettings == null)
            {
                throw new ArgumentNullException(nameof(botSettings));
            }

            this.options = botSettings.CurrentValue;
            this.appBaseUri = this.options.AppBaseUri;

            // Search service instance
            Uri serviceEndpoint = new Uri($"https://" + botSettings.CurrentValue.SEARCH_SERVICE_NAME + ".search.windows.net/");
            global::Azure.AzureKeyCredential credential = new global::Azure.AzureKeyCredential(botSettings.CurrentValue.SEARCH_QUERY_KEY);
            srchclient = new SearchClient(serviceEndpoint, botSettings.CurrentValue.SEARCH_INDEX_NAME, credential);


            // OpenAIClient instance
            
            var endpoint = new Uri(botSettings.CurrentValue.AOAI_ENDPOINT);
            var credentials = new global::Azure.AzureKeyCredential(botSettings.CurrentValue.AOAI_KEY);
            openAIClient = new OpenAIClient(endpoint, credentials);


        }

        /// <summary>
        /// Get the reply to a question asked by end user.
        /// </summary>
        /// <param name="turnContext">Context object containing information cached for a single turn of conversation with a user.</param>
        /// <param name="message">Text message.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        public async Task GetReplyToQnAAsync(
            ITurnContext<IMessageActivity> turnContext,
            IMessageActivity message)
        {
            string text = message.Text?.ToLower()?.Trim() ?? string.Empty;

            try
            {
                ResponseCardPayload payload = new ResponseCardPayload();

                if (!string.IsNullOrEmpty(message.ReplyToId) && (message.Value != null))
                {
                    payload = ((JObject)message.Value).ToObject<ResponseCardPayload>();
                }


                var answer = await ConsolidatedAnswer(text);
                //answer += "<ul><li>[Bing](https://www.bing.com/)</li><li>![Duck on a rock](http://aka.ms/Fo983c)</li></ul>";
                IMessageActivity messageActivity = MessageFactory.Attachment(ResponseCard.GetCard(answer, text, this.appBaseUri, payload));
                messageActivity.TextFormat = "markdown";
                await turnContext.SendActivityAsync(messageActivity).ConfigureAwait(false);

                // Bu alandan sonrasi degisecek
                //var queryResult = await this.questionAnswerServiceProvider.GenerateAnswerAsync(question: text, isTestKnowledgeBase: false, payload.PreviousQuestions?.Last().QnaId.ToString(), payload.PreviousQuestions?.Last().Questions.First()).ConfigureAwait(false);
                //bool answerFound = false;

                //foreach (KnowledgeBaseAnswer answerData in queryResult.Answers)
                //{
                //    bool isContextOnly = answerData.Dialog?.IsContextOnly ?? false;
                //    if (answerData.QnaId != -1 &&
                //        ((!isContextOnly && payload.PreviousQuestions == null) ||
                //            (isContextOnly && payload.PreviousQuestions != null)))
                //    {
                //        // This is the expected answer
                //        await turnContext.SendActivityAsync(MessageFactory.Attachment(ResponseCard.GetCard(answerData, text, this.appBaseUri, payload))).ConfigureAwait(false);
                //        answerFound = true;
                //        break;
                //    }
                //}

                //if (!answerFound)
                //{
                //    await turnContext.SendActivityAsync(MessageFactory.Attachment(UnrecognizedInputCard.GetCard(text))).ConfigureAwait(false);
                //}
            }
            catch (Exception ex)
            {
                // Check if knowledge base is empty and has not published yet when end user is asking a question to bot.
                if (((ErrorResponseException)ex).Response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    var knowledgeBaseId = await this.configurationProvider.GetSavedEntityDetailAsync(Constants.KnowledgeBaseEntityId).ConfigureAwait(false);
                    var hasPublished = await this.questionAnswerServiceProvider.GetInitialPublishedStatusAsync(knowledgeBaseId).ConfigureAwait(false);

                    // Check if knowledge base has not published yet.
                    if (!hasPublished)
                    {
                        this.logger.LogError(ex, "Error while fetching the qna pair: knowledge base may be empty or it has not published yet.");
                        await turnContext.SendActivityAsync(MessageFactory.Attachment(UnrecognizedInputCard.GetCard(text))).ConfigureAwait(false);
                        return;
                    }
                }

                // Throw the error at calling place, if there is any generic exception which is not caught.
                throw;
            }
        }




        async Task<string> ConsolidatedAnswer(string userMessage)
        {
            var question = userMessage;
            var context = await GetSearchResult(question);
            var promptText = CreateQuestionAndContext(question, context);

            var responseFromGPT = await GetAnswerFromGPT(promptText);

            return responseFromGPT;
        }


        // caglar - search query
        public async Task<string> GetSearchResult(string searchQuery, int? count = null, int? skip = null)
        {
           try {
            global::Azure.Search.Documents.SearchOptions searchOptions = new global::Azure.Search.Documents.SearchOptions();
            searchOptions.QueryLanguage = "tr-TR";
            searchOptions.SemanticConfigurationName = "mergenmarkdown-config";
            searchOptions.QueryCaption = "extractive";
            searchOptions.QueryAnswer = "extractive";
            searchOptions.QueryType = global::Azure.Search.Documents.Models.SearchQueryType.Semantic;
            searchOptions.QueryAnswerCount = 10;

            var returnData = srchclient.Search<SearchDocument>(searchQuery, searchOptions);

            string searchResult = string.Empty;

            if (returnData == null)
            {
                return searchResult;
            }

            var serializer = new JsonSerializer();

            using (var sr = new StreamReader(returnData.GetRawResponse().Content.ToStream()))
            using (var jsonTextReader = new JsonTextReader(sr))
            {
               
                var jsObj = serializer.Deserialize(jsonTextReader) as JObject;
                var valueSection = jsObj["value"];
                var reRankerScore = = Convert.ToDecimal(valueSection.Children().First()["content"].Value<string>());
                
                if(reRankerScore<1)
                {
                    return searchResult;  
                }
                searchResult = valueSection.Children().First()["@search.rerankerScore""].Value<string>();
                
                //searchResult = valueSection.Children().OrderByDescending(o => o["@search.rerankerScore"]).First()["content"].Value<string>();

                //jsObj["value"].Children().OrderByDescending(o => o["@search.rerankerScore"]).First()["content"].Value<string>();
               
            }


            return searchResult;
          }
          catch{
            return string.Empty;
          }

        }

        string CreateQuestionAndContext(string question, string context)
        {
            return string.Format("[Question] {0} \r\n\r\n[Context] {1} \r\n", question, context);
        }

        public async Task<string> GetAnswerFromGPT(string promptText)
        {
            //var chatMessageAsistant = new ChatMessage(ChatRole.Assistant, "You are an assistant that helps users with software and IT questions using context provided in the prompt. You only respond in Turkish and format your response in Markdown language. You will answer the [Question] below objectively in a casual and friendly tone, using the [Context] below it, and information from your memory. If the [Context] is not really relevant to the [Question], or if the [Question] is not a question at all and more of a chit chat, ignore the [Context] completely and only respond to the question with chit chat.");
            var chatMessageAsistant = new ChatMessage(ChatRole.Assistant, botSettings.CurrentValue.SettingForPrompt);
            var chatMessageUser = new ChatMessage(ChatRole.User, promptText);

            var completionOptions = new ChatCompletionsOptions
            {
                Messages = { chatMessageAsistant, chatMessageUser },

                MaxTokens = Convert.ToInt32(botSettings.CurrentValue.SettingForMaxToken),
                Temperature = float.Parse(botSettings.CurrentValue.SettingForTemperature) ,
                FrequencyPenalty = 0.5f,
                PresencePenalty = 0.0f,
                NucleusSamplingFactor = 0.95F, // Top P
                StopSequences = { "You:" }

            };

            ChatCompletions response = openAIClient.GetChatCompletions(this.options.AOAI_DEPLOYMENTID, completionOptions);

            var responseText = response.Choices.First().Message.Content;

            return responseText;

        }

        /// <summary>
        /// Method perform update operation of question and answer pair.
        /// </summary>
        /// <param name="turnContext">Context object containing information cached for a single turn of conversation with a user.</param>
        /// <param name="answer">Answer of the given question.</param>
        /// <param name="qnaPairEntity">Qna pair entity that contains question and answer information.</param>
        /// <returns>A <see cref="Task"/> of type bool where true represents question and answer pair updated successfully while false indicates failure in updating the question and answer pair.</returns>
        public async Task<bool> SaveQnAPairAsync(ITurnContext turnContext, string answer, AdaptiveSubmitActionData qnaPairEntity)
        {
            KnowledgeBaseAnswer searchResult;
            var qnaAnswerResponse = await this.questionAnswerServiceProvider.GenerateAnswerAsync(qnaPairEntity.OriginalQuestion, qnaPairEntity.IsTestKnowledgeBase).ConfigureAwait(false);
            searchResult = qnaAnswerResponse.Answers.FirstOrDefault();
            bool isSameQuestion = false;

            // Check if question exist in the knowledgebase.
            if (searchResult != null && searchResult.Questions.Count > 0)
            {
                // Check if the edited question & result returned from the knowledgebase are same.
                isSameQuestion = searchResult.Questions.First().ToUpperInvariant() == qnaPairEntity.OriginalQuestion.ToUpperInvariant();
            }

            // Edit the QnA pair if the question is exist in the knowledgebase & exactly the same question on which we are performing the action.
            if (searchResult.QnaId != -1 && isSameQuestion)
            {
                int qnaPairId = searchResult.QnaId.Value;

                this.logger.LogInformation($"Started : Question updated by: {turnContext.Activity.Conversation.AadObjectId} QnA Pair Id : {qnaPairId}");
                await this.questionAnswerServiceProvider.UpdateQnaAsync(qnaPairId, answer, turnContext.Activity.From.AadObjectId, qnaPairEntity.UpdatedQuestion, qnaPairEntity.OriginalQuestion, searchResult.Metadata).ConfigureAwait(false);
                this.logger.LogInformation($"Completed : Question updated by: {turnContext.Activity.Conversation.AadObjectId} QnA Pair Id : {qnaPairId}");

                Attachment attachment = new Attachment();
                if (qnaPairEntity.IsRichCard)
                {
                    qnaPairEntity.IsPreviewCard = false;
                    qnaPairEntity.IsTestKnowledgeBase = true;
                    attachment = MessagingExtensionQnaCard.ShowRichCard(qnaPairEntity, turnContext.Activity.From.Name, Strings.LastEditedText);
                }
                else
                {
                    qnaPairEntity.IsTestKnowledgeBase = true;
                    qnaPairEntity.Description = answer ?? throw new ArgumentNullException(nameof(answer));
                    attachment = MessagingExtensionQnaCard.ShowNormalCard(qnaPairEntity, turnContext.Activity.From.Name, actionPerformed: Strings.LastEditedText);
                }

                var activityId = this.activityStorageProvider.GetAsync(qnaAnswerResponse.Answers.First().Metadata.FirstOrDefault(x => x.Key == Constants.MetadataActivityReferenceId).Value).Result.FirstOrDefault().ActivityId;
                var updateCardActivity = new Activity(ActivityTypes.Message)
                {
                    Id = activityId ?? throw new ArgumentNullException(nameof(activityId)),
                    Conversation = turnContext.Activity.Conversation,
                    Attachments = new List<Attachment> { attachment },
                };

                // Send edited question and answer card as response.
                await turnContext.UpdateActivityAsync(updateCardActivity, cancellationToken: default).ConfigureAwait(false);
            }
            else
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validate the adaptive card fields while editing the question and answer pair.
        /// </summary>
        /// <param name="postedQnaPairEntity">Qna pair entity contains submitted card data.</param>
        /// <param name="turnContext">Context object containing information cached for a single turn of conversation with a user.</param>
        /// <returns>Envelope for Task Module Response.</returns>
        public async Task<TaskModuleResponse> EditQnAPairAsync(
            AdaptiveSubmitActionData postedQnaPairEntity,
            ITurnContext<IInvokeActivity> turnContext)
        {
            // Check if fields contains Html tags or Question and answer empty then return response with error message.
            if (Validators.IsContainsHtml(postedQnaPairEntity) || Validators.IsQnaFieldsNullOrEmpty(postedQnaPairEntity))
            {
                // Returns the card with validation errors on add QnA task module.
                return await TaskModuleActivity.GetTaskModuleResponseAsync(MessagingExtensionQnaCard.AddQuestionForm(Validators.HtmlAndQnaEmptyValidation(postedQnaPairEntity), this.appBaseUri)).ConfigureAwait(false);
            }

            if (Validators.IsRichCard(postedQnaPairEntity))
            {
                if (Validators.IsImageUrlInvalid(postedQnaPairEntity) || Validators.IsRedirectionUrlInvalid(postedQnaPairEntity))
                {
                    // Show the error message on task module response for edit QnA pair, if user has entered invalid image or redirection url.
                    return await TaskModuleActivity.GetTaskModuleResponseAsync(MessagingExtensionQnaCard.AddQuestionForm(Validators.ValidateImageAndRedirectionUrls(postedQnaPairEntity), this.appBaseUri)).ConfigureAwait(false);
                }

                string combinedDescription = QnaHelper.BuildCombinedDescriptionAsync(postedQnaPairEntity);
                postedQnaPairEntity.IsRichCard = true;

                if (postedQnaPairEntity.UpdatedQuestion?.ToUpperInvariant().Trim() == postedQnaPairEntity.OriginalQuestion?.ToUpperInvariant().Trim())
                {
                    // Save the QnA pair, return the response and closes the task module.
                    await TaskModuleActivity.GetTaskModuleResponseAsync(this.CardResponseAsync(
                        turnContext,
                        postedQnaPairEntity,
                        combinedDescription).Result).ConfigureAwait(false);
                    return default;
                }
                else
                {
                    var hasQuestionExist = await this.questionAnswerServiceProvider.QuestionExistsInKbAsync(postedQnaPairEntity.UpdatedQuestion).ConfigureAwait(false);
                    if (hasQuestionExist)
                    {
                        // Shows the error message on task module, if question already exist.
                        return await TaskModuleActivity.GetTaskModuleResponseAsync(this.CardResponseAsync(
                            turnContext,
                            postedQnaPairEntity,
                            combinedDescription).Result).ConfigureAwait(false);
                    }
                    else
                    {
                        // Save the QnA pair, return the response and closes the task module.
                        await TaskModuleActivity.GetTaskModuleResponseAsync(this.CardResponseAsync(
                            turnContext,
                            postedQnaPairEntity,
                            combinedDescription).Result).ConfigureAwait(false);
                        return default;
                    }
                }
            }
            else
            {
                // Normal card section.
                if (postedQnaPairEntity.UpdatedQuestion?.ToUpperInvariant().Trim() == postedQnaPairEntity.OriginalQuestion?.ToUpperInvariant().Trim())
                {
                    // Save the QnA pair, return the response and closes the task module.
                    await TaskModuleActivity.GetTaskModuleResponseAsync(this.CardResponseAsync(
                        turnContext,
                        postedQnaPairEntity,
                        postedQnaPairEntity.Description).Result).ConfigureAwait(false);
                    return default;
                }
                else
                {
                    var hasQuestionExist = await this.questionAnswerServiceProvider.QuestionExistsInKbAsync(postedQnaPairEntity.UpdatedQuestion).ConfigureAwait(false);
                    if (hasQuestionExist)
                    {
                        // Shows the error message on task module, if question already exist.
                        return await TaskModuleActivity.GetTaskModuleResponseAsync(this.CardResponseAsync(
                            turnContext,
                            postedQnaPairEntity,
                            postedQnaPairEntity.Description).Result).ConfigureAwait(false);
                    }
                    else
                    {
                        // Save the QnA pair, return the response and closes the task module.
                        await TaskModuleActivity.GetTaskModuleResponseAsync(this.CardResponseAsync(
                            turnContext,
                            postedQnaPairEntity,
                            postedQnaPairEntity.Description).Result).ConfigureAwait(false);
                        return default;
                    }
                }
            }
        }

        /// <summary>
        /// Return card response.
        /// </summary>
        /// <param name="turnContext">Context object containing information cached for a single turn of conversation with a user.</param>
        /// <param name="postedQnaPairEntity">Qna pair entity that contains question and answer information.</param>
        /// <param name="answer">Answer text.</param>
        /// <returns>Card attachment.</returns>
        private async Task<Attachment> CardResponseAsync(
            ITurnContext<IInvokeActivity> turnContext,
            AdaptiveSubmitActionData postedQnaPairEntity,
            string answer)
        {
            Attachment qnaAdaptiveCard = new Attachment();
            bool isSaved;

            if (postedQnaPairEntity.UpdatedQuestion?.ToUpperInvariant().Trim() == postedQnaPairEntity.OriginalQuestion?.ToUpperInvariant().Trim())
            {
                postedQnaPairEntity.IsTestKnowledgeBase = false;
                isSaved = await this.SaveQnAPairAsync(turnContext, answer, postedQnaPairEntity).ConfigureAwait(false);
                if (!isSaved)
                {
                    postedQnaPairEntity.IsTestKnowledgeBase = true;
                    await this.SaveQnAPairAsync(turnContext, answer, postedQnaPairEntity).ConfigureAwait(false);
                }
            }
            else
            {
                // Check if question exist in the production/test knowledgebase & exactly the same question.
                var hasQuestionExist = await this.questionAnswerServiceProvider.QuestionExistsInKbAsync(postedQnaPairEntity.UpdatedQuestion).ConfigureAwait(false);

                // Edit the question if it doesn't exist in the test knowledgebse.
                if (hasQuestionExist)
                {
                    // If edited question text is already exist in the test knowledgebase.
                    postedQnaPairEntity.IsQuestionAlreadyExists = true;
                }
                else
                {
                    // Save the edited question in the knowledgebase.
                    postedQnaPairEntity.IsTestKnowledgeBase = false;
                    isSaved = await this.SaveQnAPairAsync(turnContext, answer, postedQnaPairEntity).ConfigureAwait(false);
                    if (!isSaved)
                    {
                        postedQnaPairEntity.IsTestKnowledgeBase = true;
                        await this.SaveQnAPairAsync(turnContext, answer, postedQnaPairEntity).ConfigureAwait(false);
                    }
                }

                if (postedQnaPairEntity.IsQuestionAlreadyExists)
                {
                    // Response with question already exist(in test knowledgebase).
                    qnaAdaptiveCard = MessagingExtensionQnaCard.AddQuestionForm(postedQnaPairEntity, this.appBaseUri);
                }
            }

            return qnaAdaptiveCard;
        }
    }
}
