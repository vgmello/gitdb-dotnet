using FluentAssertions;
using GitDocumentDb.Internal;

namespace GitDocumentDb.Tests;

public class RecordIdValidatorTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("abc_123")]
    [InlineData("A-B-C")]
    [InlineData("file.json")]
    [InlineData("a.b.c-d_e")]
    public void Valid_ids_are_accepted(string id)
    {
        RecordIdValidator.IsValid(id).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("with\\backslash")]
    [InlineData("semi;colon")]
    public void Invalid_ids_are_rejected(string id)
    {
        RecordIdValidator.IsValid(id).Should().BeFalse();
    }

    [Fact]
    public void Too_long_ids_are_rejected()
    {
        var id = new string('a', 201);
        RecordIdValidator.IsValid(id).Should().BeFalse();
    }

    [Fact]
    public void ThrowIfInvalid_throws_ArgumentException_with_paramName()
    {
        var act = () => RecordIdValidator.ThrowIfInvalid("bad/id", "id");
        act.Should().Throw<ArgumentException>().WithParameterName("id");
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("Accounts")]
    [InlineData("my-db_1")]
    public void Valid_names_accepted(string name)
    {
        NameValidator.IsValid(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("with.dot")]
    [InlineData("with space")]
    public void Invalid_names_rejected(string name)
    {
        NameValidator.IsValid(name).Should().BeFalse();
    }
}
