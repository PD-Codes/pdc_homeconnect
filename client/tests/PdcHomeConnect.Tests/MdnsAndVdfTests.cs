using System.Buffers.Binary;
using System.Net;
using PdcHomeConnect.Core.Discovery;
using PdcHomeConnect.Core.Util;

namespace PdcHomeConnect.Tests;

public class MdnsAndVdfTests
{
    private static readonly MdnsServiceInfo Service = new()
    {
        InstanceName = "GAMING-PC",
        ServiceType = "_pdc-homeconnect._tcp",
        HostName = "pdc-gaming-pc",
        Port = 48733,
        Txt = new Dictionary<string, string> { ["id"] = "abc", ["version"] = "0.1.0" },
    };

    private static byte[] Query(string name, ushort type)
    {
        var w = new DnsWriter();
        w.Header(0, 0, 1, 0, 0, 0);
        w.Question(name, type, 1);
        return w.ToArray();
    }

    [Fact]
    public void Ptr_query_is_answered_with_service_records()
    {
        var query = DnsReader.TryParse(Query("_pdc-homeconnect._tcp.local", DnsType.Ptr))!;
        Assert.Single(query.Questions);
        var response = MdnsResponseBuilder.BuildResponse(query, Service, [IPAddress.Parse("192.168.1.20")], legacyUnicast: false);
        Assert.NotNull(response);
        Assert.Equal(0x8400, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)));  // answers: PTR
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10))); // additionals: SRV, TXT, A
    }

    [Fact]
    public void Unrelated_query_is_ignored()
    {
        var query = DnsReader.TryParse(Query("_googlecast._tcp.local", DnsType.Ptr))!;
        Assert.Null(MdnsResponseBuilder.BuildResponse(query, Service, [IPAddress.Loopback], false));
    }

    [Fact]
    public void Legacy_unicast_echoes_question_and_id()
    {
        var bytes = Query("GAMING-PC._pdc-homeconnect._tcp.local", DnsType.Srv);
        bytes[0] = 0x12;
        bytes[1] = 0x34;
        var query = DnsReader.TryParse(bytes)!;
        var response = MdnsResponseBuilder.BuildResponse(query, Service, [IPAddress.Parse("10.0.0.5")], legacyUnicast: true)!;
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(response));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4)));
        var offset = 12;
        Assert.Equal("GAMING-PC._pdc-homeconnect._tcp.local", DnsReader.ReadName(response, ref offset));
    }

    [Fact]
    public void Announcement_contains_all_records()
    {
        var packet = MdnsResponseBuilder.BuildAnnouncement(Service, [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.1.5")]);
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)));
    }

    [Fact]
    public void Vdf_parses_nested_documents()
    {
        const string text = """
        "libraryfolders"
        {
            // comment
            "0"
            {
                "path"    "C:\\Program Files (x86)\\Steam"
                "apps"
                {
                    "526870"    "12345"
                }
            }
            "1" { "path" "D:\\SteamLibrary" }
        }
        """;
        var root = Vdf.Parse(text);
        Assert.Equal(@"C:\Program Files (x86)\Steam", root.Path("libraryfolders", "0")!.GetString("path"));
        Assert.Equal(@"D:\SteamLibrary", root.Path("LibraryFolders", "1")!.GetString("PATH"));
        Assert.Equal(12345, root.Path("libraryfolders", "0", "apps")!.GetLong("526870"));
    }

    [Fact]
    public void Slugs_are_safe()
    {
        Assert.Equal("c", Slug.From("C:"));
        Assert.Equal("nvidia_geforce_rtx_4080", Slug.From("NVIDIA GeForce RTX 4080"));
        Assert.Equal("grun_uber", Slug.From("Grün über"));
        Assert.Equal("disk_usage_d", Slug.Key("disk_usage", "D:"));
    }
}
