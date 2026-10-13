using AutoMapper;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Network;
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Contract tests for the Roslyn DTO source generator: every [MapToDto] model
    /// must have a generated AzureNodeDto subclass wired into AzureNodeProfile with its TypeKey.
    /// </summary>
    public class DtoGeneratorContractTests
    {
        private static readonly Type CoreAssemblyMarker = typeof(AzureNodeDto);

        private static Type[] MapToDtoModels() =>
            CoreAssemblyMarker.Assembly.GetTypes()
                .Where(t => t.GetCustomAttributesData()
                    .Any(a => a.AttributeType.Name == "MapToDtoAttribute"))
                .ToArray();

        private static Type[] GeneratedDtos() =>
            CoreAssemblyMarker.Assembly.GetTypes()
                .Where(t => t != CoreAssemblyMarker && CoreAssemblyMarker.IsAssignableFrom(t))
                .ToArray();

        private static string TypeKeyOf(Type model) =>
            (string)(model.GetCustomAttributesData()
                .First(a => a.AttributeType.Name == "MapToDtoAttribute")
                .NamedArguments.FirstOrDefault(kvp => kvp.MemberName == "TypeKey")
                .TypedValue.Value ?? string.Empty);

        [Fact]
        public void EveryMapToDtoModelHasGeneratedDto()
        {
            var models = MapToDtoModels();
            Assert.NotEmpty(models);
            var dtos = GeneratedDtos();

            foreach (var model in models)
            {
                Assert.Contains(dtos, d => d.Name.StartsWith(model.Name, StringComparison.Ordinal));
            }
        }

        [Fact]
        public void TypeKeysAreUnique()
        {
            var keys = MapToDtoModels().Select(TypeKeyOf).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        [Fact]
        public void VnetMapsToGeneratedDtoAndBackWithTypeKey()
        {
            var mapper = new Mapper(new MapperConfiguration(cfg => cfg.AddProfile<AzureNodeProfile>()));
            var dtoType = GeneratedDtos().Single(d => d.Name.StartsWith(nameof(VirtualNetworkModel), StringComparison.Ordinal));

            var vnet = new VirtualNetworkModel { Name = "ads-vnet" };
            var dto = (AzureNodeDto)mapper.Map(vnet, typeof(VirtualNetworkModel), dtoType);

            Assert.Equal(AdsConstants.VirtualNetwork, dto.TypeKey);
            Assert.Equal("ads-vnet", dto.Name);

            var back = (VirtualNetworkModel)mapper.Map(dto, dtoType, typeof(VirtualNetworkModel));
            Assert.Equal("ads-vnet", back.Name);
        }
    }
}
