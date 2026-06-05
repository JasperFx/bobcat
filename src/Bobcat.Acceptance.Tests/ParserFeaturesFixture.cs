using Bobcat;

namespace Bobcat.Acceptance.Tests;

public class ParserFeaturesFixture : Fixture
{
    private int _base;
    private string? _body;

    [Given("the base value is {int}")]
    public void SetBase(int n) => _base = n;

    [Then("adding {int} gives {int}")]
    public int Add(int n) => _base + n;

    [When("the request body is")]
    public void SetBody(string body) => _body = body;

    [Check("the body should contain {string}")]
    public bool BodyContains(string fragment) => _body != null && _body.Contains(fragment);
}
