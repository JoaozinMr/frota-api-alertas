using FrotaAlertas.Services;

namespace FrotaAlertas.Tests;

public sealed class CorrelationIdTests
{
    [Fact]
    public void Header_tem_prioridade_sobre_body()
    {
        Assert.Equal("h", CorrelationId.Resolver("h", "b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Sem_header_usa_body(string? header)
    {
        Assert.Equal("b", CorrelationId.Resolver(header, " b "));
    }

    [Fact]
    public void Sem_header_e_body_gera_guid()
    {
        Assert.True(Guid.TryParse(CorrelationId.Resolver(null, null), out _));
    }

    [Fact]
    public void Valor_muito_longo_e_truncado()
    {
        var longo = new string('a', CorrelationId.MaxLength + 10);
        Assert.Equal(CorrelationId.MaxLength, CorrelationId.Resolver(longo, null).Length);
    }
}
