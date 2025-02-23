using AutoMapper;
using NetStone.Model.Parseables.FreeCompany.Members;

namespace MogTomeSyncFunction
{
    public class FreeCompanyMemberProfile : Profile
    {
        public FreeCompanyMemberProfile() 
        {
            CreateMap<FreeCompanyMembersEntry, FreeCompanyMember>()
                .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
                .ForMember(dest => dest.FreeCompanyRank, opt => opt.MapFrom(src => src.FreeCompanyRank))
                .ForMember(dest => dest.FreeCompanyRankIcon, opt => opt.MapFrom(src => src.FreeCompanyRankIcon))
                .ForMember(dest => dest.CharacterId, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.ActiveMember, opt => opt.MapFrom(src => true))
                .ForMember(dest => dest.LastUpdatedDate, opt => opt.MapFrom(src => DateTime.Now))
                .ForMember(dest => dest.MembershipHistory, opt => opt.MapFrom(src => $"{DateTime.Now.ToShortDateString()}-"))
                .ForMember(dest => dest.AvatarLink, opt => opt.MapFrom(src => src.Avatar.ToString()));
        }
    }
}
