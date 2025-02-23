using AutoMapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NetStone;
using NetStone.Model.Parseables.FreeCompany.Members;

namespace MogTomeSyncFunction
{
    public class MogTomeSyncFunction
    {
        private readonly ILogger _logger;
        private readonly string _connectionString;
        private readonly MongoClient _mongoClient;
        private readonly IMapper _mapper;

        public MogTomeSyncFunction(ILoggerFactory loggerFactory, IMapper mapper)
        {
            _logger = loggerFactory.CreateLogger<MogTomeSyncFunction>();
            _mapper = mapper;

            var connectionString = Environment.GetEnvironmentVariable(Constants.ConnectionStringId, EnvironmentVariableTarget.Machine);

            if (string.IsNullOrWhiteSpace(connectionString))
                connectionString = Environment.GetEnvironmentVariable(Constants.ConnectionStringId);

            _connectionString = connectionString ?? "";
            _mongoClient = new MongoClient(_connectionString);
        }

        [Function("MogTomeSyncFunction")]
        public async Task Run([TimerTrigger("0 */5 * * * *", RunOnStartup =true)] TimerInfo myTimer)
        {
            List<FreeCompanyMember> freshFreeCompanyMemberList;
            List<FreeCompanyMember> archivedFreeCompanyMemberList;

            try
            {
                var freshFreeCompanyMemberEntries = await GetFreshFreeCompanyMemberList();
                freshFreeCompanyMemberList = _mapper.Map<List<FreeCompanyMember>>(freshFreeCompanyMemberEntries);
                archivedFreeCompanyMemberList = await GetArchivedFreeCompanyMembers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to retrieve free company member list. Exception message: {message}\n{stackTrace}", ex.Message, ex.StackTrace);
                return;
            }

            try
            {
                UpdateMembersWhoHaveLeft(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
                await UpdateMembersWhoHaveJoined(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
                UpdateExistingMembers(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
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

        private static async Task<List<FreeCompanyMember>> GetArchivedFreeCompanyMembers()
        {
            var connectionString = Environment.GetEnvironmentVariable(Constants.ConnectionStringId, EnvironmentVariableTarget.Machine);
            var client = new MongoClient(connectionString);
            var membersCollection = client.GetDatabase("kupo-life").GetCollection<FreeCompanyMember>("members");
            var filter = Builders<FreeCompanyMember>.Filter.Empty;
            var freeCompanyMembers = await membersCollection.Find(filter).ToListAsync();

            return freeCompanyMembers;
        }

        private void UpdateMembersWhoHaveLeft(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
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
                var updateResult = membersCollection.BulkWrite(updates);
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
                await membersCollection.InsertManyAsync(newMembersWhoHaveJoined);

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
                }
            }
        }

        private void UpdateExistingMembers(List<FreeCompanyMember> freshFreeCompanyMemberList, List<FreeCompanyMember> archivedFreeCompanyMemberList)
        {
            var existingMembers = GetExistingMembers(freshFreeCompanyMemberList, archivedFreeCompanyMemberList);
            var membersCollection = _mongoClient.GetDatabase("kupo-life").GetCollection<FreeCompanyMember>("members");

            var updates = new List<WriteModel<FreeCompanyMember>>();
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
                }
            }

            if (updates.Count > 0)
            {
                var updateResult = membersCollection.BulkWrite(updates);
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
