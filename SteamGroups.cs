/*
 * TODO: Add queue for old member removal
 */

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;

using System.Linq;

namespace Oxide.Plugins
{
    [Info("Steam Groups", "Wulf/lukespragg", "0.3.8", ResourceId = 2085)]
    [Description("Automatically adds members of Steam group(s) to a permissions group")]
    public class SteamGroups : CovalencePlugin
    {
        #region Initialization

        private ConfigData config;
        private readonly Dictionary<string, string> steamGroups = new Dictionary<string, string>();
        private readonly Dictionary<string, string> groups = new Dictionary<string, string>();
        private readonly HashSet<string> members = new HashSet<string>();
        private readonly Queue<Member> membersQueue = new Queue<Member>();
        private readonly Regex idRegex = new Regex(@"<steamID64>(?<id>.+)</steamID64>");
        private readonly Regex pageRegex = new Regex(@"<currentPage>(?<page>.+)</currentPage>");
        private readonly Regex pagesRegex = new Regex(@"<totalPages>(?<pages>.+)</totalPages>");

        private Boolean backoffPoll = false;

        private class Member
        {
            public readonly string Id;
            public readonly string Group;

            public Member(string id, string group)
            {
                Id = id;
                Group = group;
            }
        }

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Group Setup")]
            public List<GroupInfo> GroupSetup { get; set; }

            [JsonProperty(PropertyName = "Update Interval (Seconds)")]
            public int UpdateInterval { get; set; } = 300;

            public class GroupInfo
            {
                public string Steam { get; set; } = "OxideMod";
                public string Oxide { get; set; } = "default";
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<ConfigData>();
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData
            {
                GroupSetup = new List<ConfigData.GroupInfo>
                {
                    new ConfigData.GroupInfo { Steam = "CitizenSO", Oxide = "default" },
                    new ConfigData.GroupInfo { Steam = "OxideMod", Oxide = "default" }
                },
                UpdateInterval = 300
            };
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        private void OnServerInitialized()
        {
            config = Config.ReadObject<ConfigData>();

            foreach (var group in config.GroupSetup)
            {
                if (!permission.GroupExists(group.Oxide)) permission.CreateGroup(group.Oxide, group.Oxide, 0);
                AddGroup(group.Steam, group.Oxide);
            }

            // Start the timed worker (only once, will be enabled entire plugin lifecycle)
            InitializeTimedSteamMemberCheckWorker();

            // Trigger check of Steam members immediately
            QueueWorkerThread(worker => CheckSteamGroupsForMembers());
        }

        #endregion

        #region Group Handling

        private void AddGroup(string steamGroup, string oxideGroup)
        {
            ulong result;
            var url = "http://steamcommunity.com/{0}/{1}/memberslistxml/?xml=1";
            url = string.Format(url, ulong.TryParse(steamGroup, out result) ? "gid" : "groups", steamGroup);

            steamGroups.Add(steamGroup, url);
            groups.Add(steamGroup, oxideGroup);
        }

        private void ProcessQueue()
        {
            QueueWorkerThread(worker =>
            {
                try
                {
                    var member = membersQueue.Dequeue();
                    if (permission.UserHasGroup(member.Id, groups[member.Group])) return;

                    permission.AddUserGroup(member.Id, groups[member.Group]);
                    Puts($"{member.Id} from {member.Group} added to '{groups[member.Group]}' group");
                }
                catch (Exception e)
                {
                    Puts("An error occurred while processing queue: " + e);
                }
            });
        }

        private void OnTick()
        {
            if (membersQueue.Count != 0) ProcessQueue();
            //else RemoveOldMembers();
        }

        [HookMethod("InSteamGroup")]
        public bool InSteamGroup(string id) => members.Contains(id);

        #endregion

        #region Group Cleanup

        private void RemoveOldMembers()
        {
            foreach (var group in groups)
            {
                foreach (var user in permission.GetUsersInGroup(group.Value))
                {
                    var id = Regex.Replace(user, "[^0-9]", "");
                    //membersQueue.Enqueue(new Member(id, group));
                    if (!members.Contains(id)) permission.RemoveUserGroup(id, group.Value);
                }
            }
        }

        private void ProcessRemoveQueue() => membersQueue.Dequeue();

        #endregion

        #region Member Grabbing

        [Command("steammembers")]
        private void MembersCommand(IPlayer player, string command, string[] args)
        {
            player.Reply("Checking for new Steam group members...");

            // Trigger check of Steam members immediately
            QueueWorkerThread(worker => CheckSteamGroupsForMembers());
        }

        private void InitializeTimedSteamMemberCheckWorker()
        {
            timer.Every(config.UpdateInterval, () =>
            {
                // There is no need to check if player count is 0
                if (players.Connected.Count() <= 0) return;

                // While backoff is enabled, we do not want to check members
                if (backoffPoll)
                {
                    Puts("Currently in backoff state, will not poll steam groups for members");
                    return;
                };

                try
                {
                    QueueWorkerThread(worker => CheckSteamGroupsForMembers());
                }
                catch (Exception e)
                {
                    Puts("Exception occurred: " + e);
                }
            });
        }

        private void CheckSteamGroupsForMembers()
        {
            try
            {
                Puts("Polling steam groups");
                foreach (var group in steamGroups)
                {
                    int page = 1;
                    //Puts("CheckRemoteMembers -> processing steam group: " + group.Key + ", baseUrl: " + group.Value + ", starting from page: " + page);

                    // One group at the time, no additional threading, we don't want to flood the API
                    ExecuteSteamMemberListHttpGetCall(group.Key, group.Value, page);
                }
            }
            catch (Exception e)
            {
                Puts("Exception occurred: " + e);
            }
        }

        private void ExecuteSteamMemberListHttpGetCall(string groupName, string baseUrl, int page)
        {
            try
            {
                // Custom timeout for HTTP call (in milliseconds)
                float timeout = 10000f;

                // Build base URL + page number and execute the request
                string url = string.Format("{0}&p={1}", baseUrl, page);
                webrequest.Enqueue(url, null, (code, response) => SteamMemberListCallback(code, response, groupName, baseUrl, groupName), this, Core.Libraries.RequestMethod.GET, null, timeout);
            }
            catch (Exception e)
            {
                Puts("Exception occurred: " + e);
            }
        }

        private void SteamMemberListCallback(int code, string response, string group, string baseUrl, string groupName)
        {
            if (code == 403 || code == 429)
            {
                Puts(string.Format("Steam is currently not allowing connections from your server! code={0}. Aborting this call", code));
                EnableBackoff();
                return;
            }

            if (code != 200 || response == null)
            {
                Puts(String.Format("Checking for Steam group members failed! code={0}. Aborting this call", code));
                EnableBackoff();
                return;
            }

            var ids = idRegex.Matches(response);
            foreach (Match match in ids)
            {
                var id = match.Groups["id"].Value;
                if (members.Contains(id)) continue;

                members.Add(id);
                membersQueue.Enqueue(new Member(id, group));
                //Puts("Adding " + id + " to " + group);
            }

            int currentPage;
            int totalPages;
            int.TryParse(pageRegex.Match(response)?.Groups[1]?.Value, out currentPage);
            int.TryParse(pagesRegex.Match(response)?.Groups[1]?.Value, out totalPages);

            if (currentPage != 0 && totalPages != 0 && currentPage < totalPages)
            {
                QueueWorkerThread(worker => ExecuteSteamMemberListHttpGetCall(groupName, baseUrl, currentPage + 1));
            }
        }

        private void EnableBackoff()
        {
            if (!backoffPoll)
            {
                backoffPoll = true;
                timer.Once(600f, () => QueueWorkerThread(worker => DisableBackoff()));
                Puts("Backoff state enabled");
            }
        }

        private void DisableBackoff()
        {
            Puts("Backoff state disabled");
            backoffPoll = false;
        }

        #endregion
    }
}
