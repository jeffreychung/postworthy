﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;
using Postworthy.Models.Twitter;
using Postworthy.Models.Account;
using System.Threading.Tasks;
using System.IO;
using System.Collections;
using Postworthy.Models.Repository;
using Postworthy.Models.Core;
using System.Runtime.ConstrainedExecution;
using Postworthy.Models.Streaming;
using Postworthy.Tasks.Bot.Settings;
using System.Text.RegularExpressions;
using Postworthy.Tasks.Bot.Communication;

namespace Postworthy.Tasks.Bot.Streaming
{
    public class TweetBotProcessingStep : IProcessingStep, ITweepProcessingStep, IKeywordSuggestionStep
    {
        private const string RUNTIME_REPO_KEY = "TweetBotRuntimeSettings";
        private const string COMMAND_REPO_KEY = "BotCommands";
        private const int POTENTIAL_TWEET_BUFFER_MIN = 50;
        private const int POTENTIAL_TWEET_BUFFER_MAX = 100;
        private const int POTENTIAL_TWEEP_BUFFER_MAX = 50;
        private const int MIN_TWEEP_NOTICED = 5;
        private const int TWEEP_NOTICED_AUTOMATIC = 25;
        private const int MATCHING_KEYWORDS_AUTOMATIC = 3;
        private const int MAX_TIME_BETWEEN_TWEETS = 3;
        public const int MINIMUM_KEYWORD_COUNT = 30;
        private const int MINIMUM_NEW_KEYWORD_LENGTH = 3;
        private const int MAX_KEYWORD_SUGGESTIONS = 50;
        private const int KEYWORD_FALLOUT_MINUTES = 15;
        private const int FRIEND_FALLOUT_MINUTES = 60 * 24 * 2;
        private int saveCount = 0;
        private List<string> NoTweetList = new List<string>();
        private string[] Messages = null;
        private bool OnlyWithMentions = false;
        private TextWriter log = null;
        private Tweep PrimaryTweep = null;
        private TweetBotRuntimeSettings RuntimeSettings = null;
        private CachedRepository<TweetBotRuntimeSettings> settingsRepo;
        private SimpleRepository<BotCommand> commandRepo;
        private bool ForceSimulationMode = false;
        private bool hasNewKeywordSuggestions = false;
        private IEnumerable<string> StopWords = null;
        private Regex StopWordsRegex = null;
        private Regex PunctuationRegex = new Regex(@"(\p{P})|\t|\n|\r", RegexOptions.Compiled);
        private Regex WhiteSpaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);
        private DateTime LastUpdatedTwitterSuggestedFollows = DateTime.MinValue;

        public bool SimulationMode
        {
            get
            {
                return ForceSimulationMode || (RuntimeSettings != null && RuntimeSettings.IsSimulationMode);
            }
        }

        private string RuntimeRepoKey
        {
            get
            {
                return PrimaryTweep.ScreenName + "_" + RUNTIME_REPO_KEY;
            }
        }

        public string CommandRepoKey
        {
            get
            {
                return PrimaryTweep.ScreenName + "_" + COMMAND_REPO_KEY;
            }
        }

        public void Init(string screenName, TextWriter log)
        {
            this.log = log;

            PrimaryTweep = new Tweep(UsersCollection.Single(screenName), Tweep.TweepType.None);

            settingsRepo = CachedRepository<TweetBotRuntimeSettings>.Instance(screenName);
            commandRepo = new SimpleRepository<BotCommand>(screenName);

            RuntimeSettings = (settingsRepo.Query(RuntimeRepoKey)
                ?? new List<TweetBotRuntimeSettings> { new TweetBotRuntimeSettings() }).FirstOrDefault()
                ?? new TweetBotRuntimeSettings();

            NoTweetList.Add(screenName.ToLower());
            Messages = TweetBotSettings.Get.Messages.Count == 0 ?
                null :
                Enumerable.Range(0, TweetBotSettings.Get.Messages.Count - 1)
                    .Select(i => TweetBotSettings.Get.Messages[i].Value).ToArray();
            OnlyWithMentions = TweetBotSettings.Get.Filters["OnlyWithMentions"] != null ?
                TweetBotSettings.Get.Filters["OnlyWithMentions"].Value :
                false;

            ForceSimulationMode = TweetBotSettings.Get.Settings["IsSimulationMode"] != null ?
                TweetBotSettings.Get.Settings["IsSimulationMode"].Value :
                false;

            if (ForceSimulationMode)
                log.WriteLine("{0}: Running in forced simulation mode. No actions will be taken.",
                    DateTime.Now);
            else if (SimulationMode)
                log.WriteLine("{0}: Running in automatic simulation mode to aquire a baseline. No actions will be taken for {1}hrs.",
                    DateTime.Now,
                    TweetBotRuntimeSettings.SIMULATION_MODE_HOURS);

            if (Messages == null)
                log.WriteLine("{0}: 'TweetBotSettings' configuration section is missing Messages. No responses will be sent.",
                    DateTime.Now);
            else
            {
                log.WriteLine("{0}: TweetBot will respond with: {1}",
                    DateTime.Now,
                    Environment.NewLine + string.Join(Environment.NewLine, Messages));
            }

            try
            {
                StopWords = File.OpenText("Resources/stopwords.txt")
                    .ReadToEnd()
                    .Split('\n').Select(x => x.Replace("\r", "").ToLower());
                if (StopWords.Count() > 0)
                    StopWordsRegex = new Regex("(" + string.Join("|", StopWords.Select(sw => "\\b" + sw + "\\b")) + ")", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                log.WriteLine("{0}: Stop Words: {1}",
                    DateTime.Now,
                    string.Join(",", StopWords));
            }
            catch { StopWords = new List<string>(); }
        }

        public Task<IEnumerable<Tweet>> ProcessItems(IEnumerable<Tweet> tweets)
        {
            return Task<IEnumerable<Tweet>>.Factory.StartNew(new Func<IEnumerable<Tweet>>(() =>
                {
                    RuntimeSettings.TotalTweetsProcessed += tweets.Count();

                    ExecutePendingCommands();

                    RespondToTweets(tweets);

                    FindPotentialTweets(tweets);

                    FindTweepsToFollow(tweets);

                    FindSuggestedTweeps();

                    FindKeywordsFromCurrentTweets(tweets);

                    UpdateAverageWeight(tweets);

                    SendTweets();

                    EstablishTargets();

                    DebugConsoleLog();

                    if (saveCount++ > 20)
                    {
                        SaveRuntimeSettings();
                        saveCount = 0;
                    }

                    return tweets;
                }));
        }

        public void Shutdown()
        {
            SaveRuntimeSettings();
        }

        private void SaveRuntimeSettings()
        {
            settingsRepo.Save(RuntimeRepoKey, RuntimeSettings);
        }

        private void SendTweets()
        {
            var hourOfDay = DateTime.Now.Hour + DateTime.Now.Minute / 100.0;

            if (hourOfDay > 6.29 && hourOfDay < 21.29) //Only Tweet during particular times of the day
            {
                var tweeted = RuntimeSettings.GetPastTweets();

                if (RuntimeSettings.TweetOrRetweet)
                {
                    var potentialTweets = RuntimeSettings.GetPotentialTweets();

                    RuntimeSettings.TweetOrRetweet = !RuntimeSettings.TweetOrRetweet;
                    if (potentialTweets.Length >= POTENTIAL_TWEET_BUFFER_MIN ||
                        //Because we default the LastTweetTime to the max value this will only be used after the tweet buffer initially loads up
                        (RuntimeSettings.LastTweetTime != DateTime.MaxValue && potentialTweets.Length > 0 && DateTime.Now >= RuntimeSettings.LastTweetTime.AddHours(MAX_TIME_BETWEEN_TWEETS)))
                    {
                        var tweet = potentialTweets
                            .Where(t => DateTime.Now.AddMinutes(-20) >= t.CreatedAt) //Exclude recent tweets
                            .OrderByDescending(t => t.TweetRank).First();

                        tweet.PopulateExtendedData();

                        var groups = tweeted
                            .Reverse<Tweet>()
                            .Take(50)
                            .Concat(new List<Tweet> { tweet })
                            .GroupSimilar(0.45m, log)
                            .Select(g => new TweetGroup(g))
                            .Where(g => g.GroupStatusIDs.Count() > 1);
                        var matches = groups.Where(x => x.GroupStatusIDs.Contains(tweet.StatusID));
                        if (matches.Count() > 0)
                        {
                            //Ignore Tweets that are very similar
                            RuntimeSettings.RemovePotentialTweet(tweet);
                        }
                        else
                        {
                            bool retweet = Enumerable.Range(0, 9).OrderBy(x => Guid.NewGuid()).First() > 4;
                            if (SendTweet(tweet, retweet))
                            {
                                RuntimeSettings.AddPastTweet(tweet);
                                RuntimeSettings.TweetsSentSinceLastFriendRequest++;
                                RuntimeSettings.LastTweetTime = DateTime.Now;
                                RuntimeSettings.RemovePotentialTweet(tweet);
                                //RuntimeSettings.PotentialTweets.RemoveAll(x => x.RetweetCount < GetMinRetweets());
                            }
                            else
                                RuntimeSettings.RemovePotentialTweet(tweet);
                        }
                    }
                }
                else
                {
                    var potentialReTweets = RuntimeSettings.GetPotentialTweets(true);

                    RuntimeSettings.TweetOrRetweet = !RuntimeSettings.TweetOrRetweet;
                    if (potentialReTweets.Length >= POTENTIAL_TWEET_BUFFER_MIN ||
                        //Because we default the LastTweetTime to the max value this will only be used after the tweet buffer initially loads up
                        (RuntimeSettings.LastTweetTime != DateTime.MaxValue && potentialReTweets.Length > 0 && DateTime.Now >= RuntimeSettings.LastTweetTime.AddHours(MAX_TIME_BETWEEN_TWEETS)))
                    {
                        var tweet = potentialReTweets
                            .OrderByDescending(t => t.TweetRank).First();

                        tweet.PopulateExtendedData();

                        var groups = tweeted
                            .Reverse<Tweet>()
                            .Take(50)
                            .Concat(new List<Tweet> { tweet })
                           .GroupSimilar(0.45m, log)
                           .Select(g => new TweetGroup(g))
                           .Where(g => g.GroupStatusIDs.Count() > 1);
                        var matches = groups.Where(x => x.GroupStatusIDs.Contains(tweet.StatusID));
                        if (matches.Count() > 0)
                        {
                            //Ignore Tweets that are very similar
                            RuntimeSettings.RemovePotentialTweet(tweet, true);
                        }
                        else
                        {
                            if (SendTweet(tweet, true))
                            {
                                RuntimeSettings.AddPastTweet(tweet);
                                RuntimeSettings.TweetsSentSinceLastFriendRequest++;
                                RuntimeSettings.LastTweetTime = DateTime.Now;
                                RuntimeSettings.RemovePotentialTweet(tweet, true);
                                //RuntimeSettings.PotentialReTweets.RemoveAll(x => x.RetweetCount < GetMinRetweets());
                            }
                            else
                                RuntimeSettings.RemovePotentialTweet(tweet, true);
                        }
                    }
                }
            }
        }

        private bool SendTweet(Tweet tweet, bool isRetweet)
        {
            var friendsAndFollows = PrimaryTweep.Followers().Select(x => x.ID);
            string[] ignore = (ConfigurationManager.AppSettings["Ignore"] ?? "").ToLower().Split(',');
            if (!SimulationMode)
            {

                tweet.PopulateExtendedData();
                var link = tweet.Links.OrderByDescending(x => x.ShareCount).FirstOrDefault();
                if (link != null &&
                    ignore.Where(x => link.Title.ToLower().Contains(x)).Count() == 0 && //Cant Contain an Ignore Word
                    (tweet.User.Url == null || !link.Uri.ToString().Contains(tweet.User.Url) || friendsAndFollows.Contains(tweet.User.UserID)) //Can not be from same url as user tweeting this, unless you are a friend
                    )
                {
                    if (!isRetweet)
                    {
                        string statusText = !link.Title.ToLower().StartsWith("http") ?
                            (link.Title.Length > 116 ? link.Title.Substring(0, 116) : link.Title) + " " + link.Uri.ToString()
                            :
                            link.Uri.ToString();
                        TwitterModel.Instance(PrimaryTweep.ScreenName).UpdateStatus(statusText, processStatus: false);
                    }
                    else
                        TwitterModel.Instance(PrimaryTweep.ScreenName).Retweet(tweet.StatusID);

                    return true;
                }

                return false;
            }
            else
                return true;
        }

        private void EstablishTargets()
        {
            var newFriends = false;
            if (RuntimeSettings.TweetsSentSinceLastFriendRequest >= 2)
            {
                var primaryFollowers = PrimaryTweep.Followers().Select(y => y.ID);


                var potential = RuntimeSettings.PotentialFriendRequests
                    .Where(x => x.Count > MIN_TWEEP_NOTICED)
                    .Where(x => x.Key.Type == Tweep.TweepType.None || x.Key.Type == Tweep.TweepType.Target)
                    .Where(x => !primaryFollowers.Contains(x.Key.User.UserID))
                    .Select(x => new
                    {
                        Key = x.Key,
                        Count = x.Count,
                        MatchingKeywords = RuntimeSettings.Keywords.Count(y => (x.Key.User.Description ?? "").ToLower().Contains(y.Key)),
                        Remove = new Action(() => RuntimeSettings.PotentialFriendRequests.Remove(x))
                    })
                    .OrderByDescending(x => x.Count);

                var suggested = RuntimeSettings.TwitterFollowSuggestions
                    .Where(x => !primaryFollowers.Contains(x.User.UserID))
                    .Where(x => RuntimeSettings.Keywords.Any(y => (x.User.Description ?? "").ToLower().Contains(y.Key)))
                    .Select(x => new
                    {
                        Key = x,
                        Count = 0,
                        MatchingKeywords = RuntimeSettings.Keywords.Count(y => (x.User.Description ?? "").ToLower().Contains(y.Key)),
                        Remove = new Action(() => RuntimeSettings.TwitterFollowSuggestions.Remove(x))
                    })
                    .OrderByDescending(x => x.MatchingKeywords);

                var combined = potential.Concat(suggested);

                foreach (var x in combined)
                {
                    var followers = x.Key.Followers().Select(y => y.ID);

                    //Test to see if we should make friends
                    if ((x.Count >= TWEEP_NOTICED_AUTOMATIC || //I have seen enough of you...
                        x.MatchingKeywords >= MATCHING_KEYWORDS_AUTOMATIC || //Your description says enough for you...
                        followers.Union(primaryFollowers).Count() != (followers.Count() + primaryFollowers.Count()))) //You are friends with my friends...
                    {
                        RuntimeSettings.TweetsSentSinceLastFriendRequest = 0;

                        x.Key.Type = Tweep.TweepType.Target;

                        if (!SimulationMode)
                        {
                            var follow = TwitterModel.Instance(PrimaryTweep.ScreenName).CreateFriendship(x.Key);

                            if (follow.Type == Tweep.TweepType.Following)
                            {
                                newFriends = true;
                                x.Remove();
                            }
                        }
                    }
                    else
                        x.Key.Type = Tweep.TweepType.Ignore;
                }

                if (newFriends)
                    PrimaryTweep.Followers(true);
            }
        }

        private void DebugConsoleLog()
        {
            log.WriteLine("****************************");
            log.WriteLine("****************************");
            log.WriteLine("{0}: Past Tweets: {1}",
                DateTime.Now,
                RuntimeSettings.GetPastTweets().Length);
            log.WriteLine("{0}: Potential Tweets: {1}",
                DateTime.Now,
                RuntimeSettings.GetPotentialTweets().Length);
            log.WriteLine("{0}: Potential Retweets: {1}",
                DateTime.Now,
                RuntimeSettings.GetPotentialTweets(true).Length);
            log.WriteLine("{0}: Potential Friend Requests: {1}",
                DateTime.Now,
                RuntimeSettings.PotentialFriendRequests.Count);
            log.WriteLine("{0}: Twitter Friend Suggestions: {1}",
                DateTime.Now,
                RuntimeSettings.TwitterFollowSuggestions.Count);
            log.WriteLine("{0}: Keywords: {1}",
                DateTime.Now,
                RuntimeSettings.Keywords.Count);
            log.WriteLine("{0}: Keyword Suggestions: {1}",
                DateTime.Now,
                RuntimeSettings.KeywordSuggestions.Count);
            log.WriteLine("{0}: Minimum Weight: {1:F5}",
                DateTime.Now,
                GetMinWeight());
            log.WriteLine("{0}: Minimum Retweets: {1:F2}",
                DateTime.Now,
                GetMinRetweets());
            log.WriteLine("{0}: Running {1}",
                DateTime.Now,
                SimulationMode ? "**In Simulation Mode**" : "**In the Wild**");
            log.WriteLine("****************************");
            log.WriteLine("****************************");
        }

        private void UpdateAverageWeight(IEnumerable<Tweet> tweets)
        {
            var minClout = GetMinClout();
            var minWeight = GetMinWeight();
            var friendsAndFollows = PrimaryTweep.Followers();

            var tweet_tweep_pairs = tweets
                .Select(x =>
                    x.Status.Retweeted ?
                    new
                    {
                        tweet = new Tweet(x.Status.RetweetedStatus),
                        tweep = new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None),
                        weight = x.RetweetCount / (1.0 + new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None).Clout())
                    }
                    :
                    new
                    {
                        tweet = x,
                        tweep = x.Tweep(),
                        weight = x.RetweetCount / (1.0 + x.Tweep().Clout())
                    })
                .Where(x => x.tweep.Clout() > minClout)
                .Where(x => x.weight >= minWeight);

            if (tweet_tweep_pairs.Count() > 0)
            {
                RuntimeSettings.AverageWeight = RuntimeSettings.AverageWeight > 0.0 ?
                    (RuntimeSettings.AverageWeight + tweet_tweep_pairs.Average(x => x.weight)) / 2.0 : tweet_tweep_pairs.Average(x => x.weight);
            }
            else
            {
                RuntimeSettings.AverageWeight = RuntimeSettings.AverageWeight > 0.0 ?
                    (RuntimeSettings.AverageWeight) / 2.0 : 0.0;
            }
        }

        private void FindPotentialTweets(IEnumerable<Tweet> tweets)
        {
            var minWeight = GetMinWeight();
            var minRetweets = GetMinRetweets();
            var friendsAndFollows = PrimaryTweep.Followers().Select(x => x.ID);

            var tweet_tweep_pairs = tweets
                .Select(x =>
                    //x.Status.Retweeted && !friendsAndFollows.Contains(x.Status.User.ID) ?
                    //new
                    //{
                    //    tweet = new Tweet(x.Status.RetweetedStatus),
                    //    tweep = new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None),
                    //    weight = x.RetweetCount / (1.0 + new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None).Clout())
                    //}
                    //:
                    new
                    {
                        tweet = x,
                        tweep = x.Tweep(),
                        weight = x.RetweetCount / (1.0 + x.Tweep().Clout())
                    })
                    .Where(x => Encoding.UTF8.GetByteCount(x.tweet.TweetText) == x.tweet.TweetText.Length) //Only ASCII for me...
                    .Where(x => x.weight >= minWeight)
                    .Where(x => x.tweet.RetweetCount >= minRetweets);

            if (tweet_tweep_pairs.Count() > 0)
            {
                var newPotentialRetweets = tweet_tweep_pairs
                    .Where(x => friendsAndFollows.Contains(x.tweep.User.UserID))
                    .Select(x => x.tweet)
                    .OrderByDescending(x => x.TweetRank)
                    .Take(POTENTIAL_TWEET_BUFFER_MAX)
                    .ToList();

                RuntimeSettings.AddPotentialTweets(newPotentialRetweets, true);

                var newPotentialTweets = tweet_tweep_pairs
                    .Where(x => !friendsAndFollows.Contains(x.tweep.User.UserID) &&
                        //x.tweet.Status.Entities.UserMentions.Count() == 0 &&
                        (x.tweet.Status.Entities.UrlEntities.Count() > 0 || x.tweet.Status.Entities.MediaEntities.Count() > 0))
                    .Select(x => x.tweet)
                    .OrderByDescending(x => x.RetweetCount)
                    .Take(POTENTIAL_TWEET_BUFFER_MAX)
                    .ToList();

                RuntimeSettings.AddPotentialTweets(newPotentialTweets);
            }
        }

        private void FindTweepsToFollow(IEnumerable<Tweet> tweets)
        {
            var minClout = GetMinClout();
            var minWeight = GetMinWeight();
            var friendsAndFollows = PrimaryTweep.Followers().Select(x => x.ID);
            var tweet_tweep_pairs = tweets
                .Select(x =>
                    x.Status.Retweeted && !friendsAndFollows.Contains(x.Status.User.UserID) ?
                    new
                    {
                        tweet = new Tweet(x.Status.RetweetedStatus),
                        tweep = new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None),
                        weight = x.RetweetCount / (1.0 + new Tweep(x.Status.RetweetedStatus.User, Tweep.TweepType.None).Clout())
                    }
                    :
                    new
                    {
                        tweet = x,
                        tweep = x.Tweep(),
                        weight = x.RetweetCount / (1.0 + x.Tweep().Clout())
                    })
                .Where(x => !friendsAndFollows.Contains(x.tweep.User.UserID))
                .Where(x => x.tweep.User.LangResponse == PrimaryTweep.User.LangResponse)
                .Where(x => x.tweep.Clout() > minClout)
                .Where(x => x.weight >= minWeight);

            //Update Existing
            var existingTweeps = tweet_tweep_pairs.Select(x => x.tweep);
            foreach (var x in existingTweeps)
            {
                var existing = RuntimeSettings.PotentialFriendRequests
                    .Where(t => t.Key.Equals(x));
                foreach (var t in existing)
                {
                    if (t.Key.Type != Tweep.TweepType.Target && t.Key.Type != Tweep.TweepType.IgnoreAlways)
                        t.Key.Type = Tweep.TweepType.None;

                    t.Count++;
                }
            }

            //Add New
            var addNew = tweet_tweep_pairs
                    .Select(x => x.tweep)
                    .Except(RuntimeSettings.PotentialFriendRequests.Select(x => x.Key));

            foreach (var x in addNew)
            {
                RuntimeSettings.PotentialFriendRequests.Add(new CountableItem<Tweep>(x, 1));
            }

            //Limit
            RuntimeSettings.PotentialFriendRequests = RuntimeSettings.PotentialFriendRequests
                .Where(x => x.Key.Type == Tweep.TweepType.Target || x.LastModifiedTime.AddHours(FRIEND_FALLOUT_MINUTES) > DateTime.Now) //Only watch them for a limited time
                .Where(x => !friendsAndFollows.Contains(x.Key.User.UserID))
                .OrderByDescending(x => x.Key.Clout())
                .Take(POTENTIAL_TWEEP_BUFFER_MAX)
                .ToList();
        }

        private void FindSuggestedTweeps()
        {
            if (LastUpdatedTwitterSuggestedFollows.AddHours(1) <= DateTime.Now)
            {
                try
                {
                    var friendsAndFollows = PrimaryTweep.Followers().Select(x => x.ID);
                    var keywords = RuntimeSettings.Keywords.Select(x => x.Key).Union(GetKeywordSuggestions());
                    //Func<Tweep, bool> where = (x => x.User.Description != null && keywords.Where(w => x.User.Description.ToLower().Contains(w)).Count() >= 2);
                    Func<Tweep, bool> where = (x => !friendsAndFollows.Contains(x.User.UserID));

                    var results = TwitterModel.Instance(PrimaryTweep.ScreenName).GetSuggestedFollowsForPrimaryUser()
                        .Where(where)
                        .ToList();

                    RuntimeSettings.TwitterFollowSuggestions = RuntimeSettings.TwitterFollowSuggestions
                        .Where(where)
                        .Union(results).ToList();
                }
                catch (Exception ex)
                {
                    log.WriteLine("{0}: Error from FindSuggestedTweeps: {1}",
                        DateTime.Now,
                        ex.ToString());
                }
                finally
                {
                    LastUpdatedTwitterSuggestedFollows = DateTime.Now;
                }
            }
        }

        private int GetMinClout()
        {
            /*
            var friends = PrimaryTweep.Followers().Select(x=>x.Value)
                .Where(x => x.Type == Tweep.TweepType.Follower || x.Type == Tweep.TweepType.Mutual);
            double minClout = friends.Count() + 1.0;
            return (int)Math.Max(minClout, friends.Count() > 0 ? Math.Floor(friends.Average(x => x.Clout())) : 0);
            */
            return PrimaryTweep.Clout();
        }

        private double GetMinWeight()
        {
            if (RuntimeSettings.AverageWeight < 0.00001) RuntimeSettings.AverageWeight = 0.0;
            return RuntimeSettings.AverageWeight;
        }

        private double GetMinRetweets()
        {
            return RuntimeSettings.MinimumRetweetLevel;
        }

        private IEnumerable<Tweet> RespondToTweets(IEnumerable<Tweet> tweets)
        {

            var repliedTo = new List<Tweet>();
            if (Messages != null)
            {
                foreach (var t in tweets)
                {
                    string tweetedBy = t.User.ScreenName.ToLower();
                    if (!NoTweetList.Any(x => x == tweetedBy) && //Don't bug people with constant retweets
                        !t.TweetText.ToLower().Contains(NoTweetList[0]) && //Don't bug them even if they are mentioned in the tweet
                        (!OnlyWithMentions || t.Status.Entities.UserMentionEntities.Count > 0) //OPTIONAL: Only respond to tweets that mention someone
                        )
                    {
                        //Dont want to keep hitting the same person over and over so add them to the ignore list
                        NoTweetList.Add(tweetedBy);
                        //If they were mentioned in a tweet they get ignored in the future just in case they reply
                        NoTweetList.AddRange(t.Status.Entities.UserMentionEntities.Where(um => !string.IsNullOrEmpty(um.ScreenName)).Select(um => um.ScreenName));

                        string message = "";
                        if (t.User.FollowersCount > 9999)
                            //TODO: It would be very cool to have the code branch here for custom tweets to popular twitter accounts
                            //IDEA: Maybe have it text you for a response
                            message = Messages.OrderBy(x => Guid.NewGuid()).FirstOrDefault();
                        else
                            //Randomly select response from list of possible responses
                            message = Messages.OrderBy(x => Guid.NewGuid()).FirstOrDefault();

                        //Tweet it
                        try
                        {
                            TwitterModel.Instance(PrimaryTweep.ScreenName).UpdateStatus(message + " RT @" + t.User.ScreenName + " " + t.TweetText, processStatus: false);
                        }
                        catch (Exception ex) { log.WriteLine("{0}: TweetBot Error: {1}", DateTime.Now, ex.ToString()); }

                        repliedTo.Add(t);

                        //Wait at least 1 minute between tweets so it doesnt look bot-ish with fast retweets
                        //Add some extra random timing somewhere between 0-2 minutes
                        //The shortest wait will be 1 minute the longest will be 3
                        int randomTime = 60000 + (1000 * Enumerable.Range(0, 120).OrderBy(x => Guid.NewGuid()).FirstOrDefault());
                        System.Threading.Thread.Sleep(randomTime);
                    }
                }
            }
            return repliedTo;
        }

        private void FindKeywordsFromCurrentTweets(IEnumerable<Tweet> tweets)
        {
            long nothing;
            //Strip Punctuation, Strip Stop Words, Force Lowercase
            var cleanedTweets = tweets.Select(t => WhiteSpaceRegex.Replace(StopWordsRegex.Replace(PunctuationRegex.Replace(t.TweetText, " "), ""), " ").ToLower()).ToList();
            var cleanedPastTweets = RuntimeSettings.GetPastTweets().Select(t => WhiteSpaceRegex.Replace(StopWordsRegex.Replace(PunctuationRegex.Replace(t.TweetText, " "), ""), " ").ToLower()).ToList();


            //Get All Words Added by User (From Config & Dashboard)
            var manuallyAddedWords = RuntimeSettings.KeywordsToIgnore.Concat(RuntimeSettings.KeywordsManuallyAdded).Select(x => x.ToLower());

            //Get Current Keyword Counts
            var currentKeywords = manuallyAddedWords.Select(maw =>
                new
                {
                    Word = maw,
                    Count = tweets.Select(t => t.TweetText).Select(t => new Regex(maw).Matches(t).Count).Sum()
                }).ToList();

            //Update Master Keyword List
            foreach (var w in currentKeywords)
            {
                var item = RuntimeSettings.Keywords.Where(x => x.Key == w.Word).FirstOrDefault();
                if (item != null)
                    item.Count += w.Count;
                else
                    RuntimeSettings.Keywords.Add(new CountableItem(w.Word, w.Count));
            }

            //Only words we are tracking from config
            RuntimeSettings.Keywords = RuntimeSettings.Keywords.Where(w => manuallyAddedWords.Contains(w.Key)).ToList();

            var cleanedAll = cleanedTweets.Concat(cleanedPastTweets);

            var words = cleanedTweets
                .SelectMany(t => t.Split(' '))
                .Select(w => w.Trim())
                .Where(w => !long.TryParse(w, out nothing)) //Ignore Numbers
                .Where(x => x.Length >= MINIMUM_NEW_KEYWORD_LENGTH) //Must be Minimum Length
                .Except(RuntimeSettings.KeywordsToIgnore.SelectMany(y => y.Split(' ').Concat(new string[] { y }))) //Exclude Ignore Words, which are current keywords
                //.Except(StopWords) //Exclude Stop Words
                .Where(x => !x.StartsWith("http")) //No URLs
                .Where(x => Encoding.UTF8.GetByteCount(x) == x.Length) //Only ASCII for me...
                .ToList();

            //Create pairs of words for phrase searching
            //var wordPairs = words.SelectMany(w => words.Distinct().Where(x => x != w).Select(w2 => (w + " " + w2).Trim())).ToList();
            var wordPairs = new List<CountableItem<string>>();
            Regex regexObj = new Regex(
                @"(     # Match and capture in backreference no. 1:
                 \w+    # one or more alphanumeric characters
                 \s+    # one or more whitespace characters.
                )       # End of capturing group 1.
                (?=     # Assert that there follows...
                 (\w+)  # another word; capture that into backref 2.
                )       # End of lookahead.",
                RegexOptions.IgnorePatternWhitespace);
            foreach (var t in cleanedTweets)
            {
                var matchResult = regexObj.Match(t);
                while (matchResult.Success)
                {
                    var pair = matchResult.Groups[1].Value + matchResult.Groups[2].Value;
                    var match = wordPairs.SingleOrDefault(x => x.Key == pair);

                    if (match != null)
                        match.Count++;
                    else
                        wordPairs.Add(new CountableItem<string>(pair, 1));

                    matchResult = matchResult.NextMatch();
                }
            }

            var validPairs = wordPairs.Where(x => x.Count > 1).Select(x => x.Key);

            //Match phrases in tweet text (must match in more than 1 tweet to be valid)
            //var validPairs = wordPairs.Where(wp => cleanedAll.Count(t => t.IndexOf(wp, StringComparison.Ordinal) > -1) > 1).ToList();

            //Remove words that are found in a phrase and union with phrases
            words = words.Except(validPairs.SelectMany(x => x.Split(' '))).Concat(validPairs).ToList();

            //For Later
            //var oldSuggestionCount = RuntimeSettings.KeywordSuggestions.Where(x => x.Count >= MINIMUM_KEYWORD_COUNT).Count();

            var keywords = words
                .GroupBy(w => w) //Group Similar Words
                .Select(g => new { Word = g.Key, Count = g.Count() }) // Get Keyword Counts
                .ToList();

            //Update Master Keyword List
            foreach (var w in keywords)
            {
                var item = RuntimeSettings.KeywordSuggestions.Where(x => x.Key == w.Word).FirstOrDefault();
                if (item != null)
                    item.Count += w.Count;
                else
                    RuntimeSettings.KeywordSuggestions.Add(new CountableItem(w.Word, w.Count));
            }

            RuntimeSettings.KeywordSuggestions = RuntimeSettings.KeywordSuggestions
                .Where(x => !long.TryParse(x.Key, out nothing)) //Ignore Numbers
                //.Where(x => !StopWords.Contains(x.Key)) //Exclude Stop Words
                .Where(x => !RuntimeSettings.KeywordsManuallyIgnored.Contains(x.Key)) //Exclude Manually Ignored Words/Phrases
                .Where(x => !manuallyAddedWords.SelectMany(y => y.Split(' ').Concat(new string[] { y })).Contains(x.Key)) //Exclude Manually Added Words
                .Where(x => !x.Key.StartsWith("http")) //No URLs
                .Where(x => x.Key.Length >= MINIMUM_NEW_KEYWORD_LENGTH) //Must be Minimum Length
                .Where(x => Encoding.UTF8.GetByteCount(x.Key) == x.Key.Length) //Only ASCII for me...
                .Where(x =>
                    (x.Count >= MINIMUM_KEYWORD_COUNT || x.LastModifiedTime.AddMinutes(KEYWORD_FALLOUT_MINUTES) >= DateTime.Now)  //Fallout if not seen frequently unless beyond threshold
                    && (x.LastModifiedTime.AddHours(24) > DateTime.Now)) //No matter what it must be seen within the last 24hrs
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.LastModifiedTime)
                .ThenByDescending(x => x.Key.Length)
                .Take(MAX_KEYWORD_SUGGESTIONS)
                .ToList();

            //For Comparison
            //var newSuggestionCount = RuntimeSettings.KeywordSuggestions.Where(x => x.Count >= MINIMUM_KEYWORD_COUNT).Count();

            //If we have difference then set the flag
            //if (newSuggestionCount != oldSuggestionCount)
            //    hasNewKeywordSuggestions = true;
        }

        public List<string> GetKeywordSuggestions()
        {
            return RuntimeSettings.KeywordsManuallyAdded;
            /*
            return RuntimeSettings.KeywordSuggestions
                .Where(x => x.Count >= MINIMUM_KEYWORD_COUNT)
                .Select(x => x.Key)
                .Union(RuntimeSettings.KeywordsManuallyAdded)
                .ToList();
             */
        }

        public void ResetHasNewKeywordSuggestions()
        {
            hasNewKeywordSuggestions = false;
        }

        public bool HasNewKeywordSuggestions()
        {
            return hasNewKeywordSuggestions;
        }

        public void SetIgnoreKeywords(List<string> keywords)
        {
            RuntimeSettings.KeywordsToIgnore = keywords;
        }

        public Task<IEnumerable<LazyLoader<Tweep>>> ProcessTweeps(IEnumerable<LazyLoader<Tweep>> tweeps)
        {
            return Task<IEnumerable<LazyLoader<Tweep>>>.Factory.StartNew(new Func<IEnumerable<LazyLoader<Tweep>>>(() =>
                {
                    PrimaryTweep.OverrideFollowers(tweeps.ToList());
                    return tweeps;
                }));
        }

        private void ExecutePendingCommands()
        {
            var unexecutedCommands = commandRepo.Query(CommandRepoKey, where: x => !x.HasBeenExecuted);
            if (unexecutedCommands != null)
            {
                foreach (var command in unexecutedCommands)
                {
                    switch (command.Command)
                    {
                        case BotCommand.CommandType.Refresh:
                            //We dont have to do anything since the data is saved to repo below...
                            break;
                        case BotCommand.CommandType.AddKeyword:
                            if (!RuntimeSettings.KeywordsManuallyAdded.Contains(command.Value))
                            {
                                RuntimeSettings.KeywordsManuallyAdded.Add(command.Value);
                                RuntimeSettings.Keywords.Add(new CountableItem(command.Value, 0));
                                RuntimeSettings.KeywordsManuallyIgnored.Remove(command.Value);
                                hasNewKeywordSuggestions = true;
                            }
                            break;
                        case BotCommand.CommandType.IgnoreKeyword:
                            if (!RuntimeSettings.KeywordsManuallyIgnored.Contains(command.Value))
                            {
                                RuntimeSettings.KeywordsManuallyIgnored.Add(command.Value);

                                RuntimeSettings.Keywords.Remove(RuntimeSettings.Keywords.Where(x => x.Key == command.Value).FirstOrDefault());

                                var shouldResetKeywords = RuntimeSettings.KeywordsManuallyAdded.Remove(command.Value);
                                //|| RuntimeSettings.KeywordSuggestions.Remove(RuntimeSettings.KeywordSuggestions.Where(x => x.Key == command.Value).FirstOrDefault());

                                if (shouldResetKeywords)
                                    hasNewKeywordSuggestions = true;
                            }
                            break;
                        case BotCommand.CommandType.IgnoreTweep:
                            var tweepIgnore = RuntimeSettings.PotentialFriendRequests.Where(x => x.Key.UniqueKey == command.Value).FirstOrDefault();
                            if (tweepIgnore != null)
                                tweepIgnore.Key.Type = Tweep.TweepType.IgnoreAlways;
                            break;
                        case BotCommand.CommandType.TargetTweep:
                            var tweepTarget = RuntimeSettings.PotentialFriendRequests.Where(x => x.Key.UniqueKey == command.Value).FirstOrDefault();
                            if (tweepTarget != null)
                                tweepTarget.Key.Type = Tweep.TweepType.Target;
                            break;
                        case BotCommand.CommandType.RemovePotentialRetweet:
                            var retweet = RuntimeSettings.GetPotentialTweets(true).Where(x => x.UniqueKey == command.Value).FirstOrDefault();
                            RuntimeSettings.RemovePotentialTweet(retweet, true);
                            break;
                        case BotCommand.CommandType.RemovePotentialTweet:
                            var tweet = RuntimeSettings.GetPotentialTweets().Where(x => x.UniqueKey == command.Value).FirstOrDefault();
                            RuntimeSettings.RemovePotentialTweet(tweet);
                            break;
                    }

                    command.HasBeenExecuted = true;
                }

                commandRepo.Save(CommandRepoKey, unexecutedCommands);
                settingsRepo.Save(RuntimeRepoKey, RuntimeSettings);
            }
        }
    }
}

