using AutoMapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NetStone;
using NetStone.Model.Parseables.FreeCompany.Members;
using System.Text.Json;

namespace MogTomeSyncFunction
{
    public class MogTomeSyncFunction
    {
        private readonly ILogger _logger;
        private string _connectionString;
        private MongoClient _mongoClient;
        private readonly IMapper _mapper;
        private readonly HttpClient _httpClient;
        private readonly string _mogTomeApiUrl;
        private readonly string _mogTomeApiKey;

        public MogTomeSyncFunction(ILoggerFactory loggerFactory, IMapper mapper, HttpClient httpClient)
        {
            _logger = loggerFactory.CreateLogger<MogTomeSyncFunction>();
            _mapper = mapper;
            _connectionString = Environment.GetEnvironmentVariable(Constants.ConnectionStringId, EnvironmentVariableTarget.Process) ?? "";
            _mongoClient = new MongoClient(_connectionString);
            _httpClient = httpClient;
            _mogTomeApiUrl = Environment.GetEnvironmentVariable(Constants.MogTomeApiUrlId, EnvironmentVariableTarget.Process) ?? "";
            _mogTomeApiKey = Environment.GetEnvironmentVariable(Constants.MogTomeApiKeyId, EnvironmentVariableTarget.Process) ?? "";
        }

        [Function("MogTomeSyncFunction")]
        public async Task Run([TimerTrigger("0 */15 * * * *", RunOnStartup = true)] TimerInfo myTimer)
        {
            List<FreeCompanyMember> freshFreeCompanyMemberList;
            List<FreeCompanyMember> archivedFreeCompanyMemberList;

            try
            {
                var freshFreeCompanyMemberEntries = await GetFreshFreeCompanyMemberList();
                freshFreeCompanyMemberList = _mapper.Map<List<FreeCompanyMember>>(freshFreeCompanyMemberEntries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to retrieve free company member list from lodestone. Exception message: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
                return;
            }

            try
            {
                archivedFreeCompanyMemberList = await GetArchivedFreeCompanyMembers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to retrieve free company member list from mongo. Exception message: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
                return;
            }

            if (freshFreeCompanyMemberList is null || freshFreeCompanyMemberList.Count < 10)
            {
                _logger.LogError("Unable to retrieve a healthy free company member list from lodestone.");
                return;
            }

            try
            {
                await UpdateMembersWhoHaveLeft(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
                await UpdateMembersWhoHaveJoined(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
                await UpdateExistingMembers(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to update data in mongo. Exception message: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
                return;
            }

            _logger.LogInformation($"Function completed successfully");
        }

        private static async Task<List<FreeCompanyMembersEntry>> GetFreshFreeCompanyMemberList()
        {
            List<FreeCompanyMembersEntry> members = [];
            var lodestoneClient = await LodestoneClient.GetClientAsync();
            var freeCompanyMembers = await lodestoneClient.GetFreeCompanyMembers(Constants.KupoLifeId);

            while (freeCompanyMembers != null)
            {
                members.AddRange(freeCompanyMembers.Members);
                freeCompanyMembers = await freeCompanyMembers.GetNextPage();
            }

            return members;
        }

        private async Task<List<FreeCompanyMember>> GetArchivedFreeCompanyMembers()
        {
            var membersCollection = _mongoClient.GetDatabase("kupo-life").GetCollection<FreeCompanyMember>("members");
            var filter = Builders<FreeCompanyMember>.Filter.Empty;
            var freeCompanyMembers = await membersCollection.Find(filter).ToListAsync();

            return freeCompanyMembers;
        }

        private async Task UpdateMembersWhoHaveLeft(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var membersWhoHaveLeft = GetMembersWhoHaveLeft(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
            var idsOfMembersWhoHaveLeft = membersWhoHaveLeft.Select(member => member.CharacterId).ToList();
            var membersCollection = _mongoClient.GetDatabase("kupo-life").GetCollection<FreeCompanyMember>("members");

            var updates = new List<WriteModel<FreeCompanyMember>>();
            foreach (var member in membersWhoHaveLeft)
            {
                var filter = Builders<FreeCompanyMember>.Filter.Eq("CharacterId", member.CharacterId);
                var update = Builders<FreeCompanyMember>.Update
                    .Set(member => member.MembershipHistory, $"{member.MembershipHistory}{DateTime.Now.ToShortDateString()}")
                    .Set(member => member.LastUpdatedDate, DateTime.Now)
                    .Set(member => member.ActiveMember, false);

                var updateModel = new UpdateOneModel<FreeCompanyMember>(filter, update);
                updates.Add(updateModel);
            }

            if (updates.Count > 0)
            {
                var updateResult = await membersCollection.BulkWriteAsync(updates);
            }
        }

        private async Task UpdateMembersWhoHaveJoined(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var newMembersWhoHaveJoined = GetNewMembersWhoHaveJoined(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
            var returningMembersWhoHaveJoined = GetReturningMembersWhoHaveJoined(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);

            if (newMembersWhoHaveJoined.Count == 0 && returningMembersWhoHaveJoined.Count == 0)
                return;

            var membersCollection = _mongoClient.GetDatabase("kupo-life").GetCollection<FreeCompanyMember>("members");

            // Insert brand new members
            if (newMembersWhoHaveJoined.Count > 0)
            {
                await membersCollection.InsertManyAsync(newMembersWhoHaveJoined);

                try
                {
                    await SendEvents(newMembersWhoHaveJoined.Select(member => $"{member.Name} has joined the Free Company"), EventType.MemberJoined);
                } 
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to create events for new members. Exception message: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
                }
            }

            // Update documents for rejoining members
            if (returningMembersWhoHaveJoined.Count > 0)
            {
                var updates = new List<WriteModel<FreeCompanyMember>>();
                foreach (var member in returningMembersWhoHaveJoined)
                {
                    var filter = Builders<FreeCompanyMember>.Filter.Eq("CharacterId", member.CharacterId);
                    var update = Builders<FreeCompanyMember>.Update
                        .Set(member => member.Name, member.Name)
                        .Set(member => member.FreeCompanyRank, member.FreeCompanyRank)
                        .Set(member => member.FreeCompanyRankIcon, member.FreeCompanyRankIcon)
                        .Set(member => member.MembershipHistory, $"{member.MembershipHistory}+{DateTime.Now.ToShortDateString()}-")
                        .Set(member => member.LastUpdatedDate, DateTime.Now)
                        .Set(member => member.ActiveMember, true)
                        .Set(member => member.AvatarLink, member.AvatarLink);

                    var updateModel = new UpdateOneModel<FreeCompanyMember>(filter, update);
                    updates.Add(updateModel);
                }

                if (updates.Count > 0)
                {
                    var updateResult = membersCollection.BulkWrite(updates);
                    await SendEvents(returningMembersWhoHaveJoined.Select(member => $"{member.Name} has rejoined the Free Company"), EventType.MemberRejoined);
                }
            }
        }

        private async Task UpdateExistingMembers(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var existingMembers = GetExistingMembers(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
            var membersCollection = _mongoClient.GetDatabase("kupo-life").GetCollection<FreeCompanyMember>("members");

            var updates = new List<WriteModel<FreeCompanyMember>>();
            var freeCompanyMemberChangeRecords = new List<FreeCompanyMemberChangeRecord>();
            foreach (var member in existingMembers)
            {
                var currentName = freshFreeCompanyMemberList.First(freshMember => freshMember.CharacterId == member.CharacterId).Name;
                var currentAvatarLink = freshFreeCompanyMemberList.First(freshMember => freshMember.CharacterId == member.CharacterId).AvatarLink;
                var currentRank = freshFreeCompanyMemberList.First(freshMember => freshMember.CharacterId == member.CharacterId).FreeCompanyRank;
                var currentRankIcon = freshFreeCompanyMemberList.First(freshMember => freshMember.CharacterId == member.CharacterId).FreeCompanyRankIcon;

                if (currentName != member.Name || currentAvatarLink != member.AvatarLink || currentRank != member.FreeCompanyRank || currentRankIcon != member.FreeCompanyRankIcon)
                {
                    var filter = Builders<FreeCompanyMember>.Filter.Eq("CharacterId", member.CharacterId);
                    var update = Builders<FreeCompanyMember>.Update
                        .Set(member => member.Name, currentName)
                        .Set(member => member.LastUpdatedDate, DateTime.Now)
                        .Set(member => member.AvatarLink, currentAvatarLink)
                        .Set(member => member.FreeCompanyRank, currentRank)
                        .Set(member => member.FreeCompanyRankIcon, currentRankIcon);

                    var updateModel = new UpdateOneModel<FreeCompanyMember>(filter, update);
                    updates.Add(updateModel);

                    freeCompanyMemberChangeRecords.Add(new FreeCompanyMemberChangeRecord
                    {
                        HistoricalMemberData = member,
                        CurrentName = currentName,
                        CurrentRank = currentRank
                    });
                }
            }

            if (updates.Count > 0)
            {
                var updateResult = await membersCollection.BulkWriteAsync(updates);
            }

            await ProcessExistingMemberUpdateEvents(freeCompanyMemberChangeRecords);
        }

        private async Task ProcessExistingMemberUpdateEvents(IEnumerable<FreeCompanyMemberChangeRecord> freeCompanyMemberChangeRecords)
        {
            List<string> rankPromotionEventMessages = [];
            List<string> nameChangeEventMessages = [];

            foreach (var record in freeCompanyMemberChangeRecords)
            {
                var currentName = record.CurrentName;
                var historicalName = record.HistoricalMemberData.Name;
                var currentRank = record.CurrentRank;
                var historicalRank = record.HistoricalMemberData.FreeCompanyRank;

                if (currentName != historicalName)
                {
                    nameChangeEventMessages.Add($"{historicalName} has changed their name to {currentName}");
                }

                if (currentRank != historicalRank && RankChangeIsPromotion(currentRank, historicalRank))
                {
                    rankPromotionEventMessages.Add($"{currentName} has been promoted to {currentRank}");
                }
            }

            if (nameChangeEventMessages.Count > 0)
            {
                await SendEvents(nameChangeEventMessages, EventType.NameChanged);
            }

            if (rankPromotionEventMessages.Count > 0)
            {
                await SendEvents(rankPromotionEventMessages, EventType.RankPromoted);
            }
        }

        public class FreeCompanyMemberChangeRecord
        {
            public FreeCompanyMember HistoricalMemberData { get; set; }
            public string CurrentName { get; set; }
            public string CurrentRank { get; set; }
        }

        private static bool RankChangeIsPromotion(string currentRank, string historicalRank)
        {
            var FreeCompanyMemberRanks = new Dictionary<string, int>
            {
                { "Mandragora", 1 },
                { "Coeurl Hunter", 2 },
                { "Paissa Trainer", 3 },
                { "Moogle Knight", 4 }
            };

            if (FreeCompanyMemberRanks.TryGetValue(currentRank, out int currentRankValue) && FreeCompanyMemberRanks.TryGetValue(historicalRank, out int historicalRankValue))
            {
                return currentRankValue > historicalRankValue;
            }

            return false;
        }

        private async Task SendEvents(IEnumerable<string> eventMessages, EventType eventType)
        {
            try
            {
                List<Event> events = [];
                foreach (var message in eventMessages)
                {
                    var newMemberEvent = new Event
                    {
                        Id = Guid.NewGuid(),
                        Text = message,
                        Type = eventType.ToString(),
                        Date = DateTime.Now
                    };

                    events.Add(newMemberEvent);
                }

                // Add the new events to the mongodb collection
                var eventsCollection = _mongoClient.GetDatabase("kupo-life").GetCollection<Event>("events");
                await eventsCollection.InsertManyAsync(events);

                // Broadcast the new events to signalr listeners via mogtomeapi
                var jsonContent = JsonSerializer.Serialize(events);
                var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
                httpContent.Headers.Add("X-API-KEY", _mogTomeApiKey);
                await _httpClient.PostAsync($"{_mogTomeApiUrl}/events/create-event", httpContent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to send events to mogtomeapi. Exception message: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
            }
        }

        private static List<FreeCompanyMember> GetMembersWhoHaveLeft(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var membersWhoHaveLeft = archivedFreeCompanyMemberList
                .Where(member => member.ActiveMember)
                .Where(member => freshFreeCompanyMemberList.Any(freshMember => freshMember.CharacterId.Equals(member.CharacterId)) == false)
                .ToList();

            return membersWhoHaveLeft;
        }

        private static List<FreeCompanyMember> GetNewMembersWhoHaveJoined(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var membersWhoHaveJoined = freshFreeCompanyMemberList
                .Where(member => archivedFreeCompanyMemberList.Any(historicalMember => historicalMember.CharacterId.Equals(member.CharacterId)) == false)
                .ToList();

            return membersWhoHaveJoined;
        }

        private static List<FreeCompanyMember> GetReturningMembersWhoHaveJoined(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var membersWhoHaveRejoined = archivedFreeCompanyMemberList
                .Where(member => freshFreeCompanyMemberList.Any(freshMember => freshMember.CharacterId.Equals(member.CharacterId) && member.ActiveMember == false))
                .ToList();

            return membersWhoHaveRejoined;
        }

        private static List<FreeCompanyMember> GetExistingMembers(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var existingMembers = archivedFreeCompanyMemberList
                .Where(member => member.ActiveMember)
                .Where(member => freshFreeCompanyMemberList.Any(freshMember => freshMember.CharacterId.Equals(member.CharacterId)))
                .ToList();

            return existingMembers;
        }
    }
}
