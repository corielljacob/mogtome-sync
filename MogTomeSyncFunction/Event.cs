using MongoDB.Bson;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MogTomeSyncFunction
{
    public class Event
    {
        public ObjectId Id { get; set; }
        public string Text { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Type { get; set; }
    }

    public enum EventType
    {
        MemberJoined,
        MemberRejoined,
        RankPromoted,
        NameChanged
    }
}
