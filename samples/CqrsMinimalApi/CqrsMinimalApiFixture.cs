using Bobcat;
using Bobcat.Alba;

namespace CqrsMinimalApi.Tests;

// Reuses the host's Student / CreateStudentRequest / CreateStudentResponse /
// UpdateStudentRequest types directly via the project reference, so the
// fixture and endpoints can't drift on shape. (Earlier this file shadowed
// those types with locally-declared records that didn't match the API.)
[FixtureTitle("CQRS Minimal API Students")]
public class CqrsMinimalApiFixture : Fixture
{
    private Guid _studentId;
    private int _lastStatusCode;
    private List<Student> _students = [];

    public override Task SetUp()
    {
        _studentId = Guid.Empty;
        _lastStatusCode = 0;
        _students = [];
        return Task.CompletedTask;
    }

    [Given("I create a student with name {string} and email {string}")]
    public Task GivenCreateStudent(string name, string email) => CreateStudentCore(name, email);

    [When("I create a student with name {string} and email {string}")]
    public Task WhenCreateStudent(string name, string email) => CreateStudentCore(name, email);

    private async Task CreateStudentCore(string name, string email)
    {
        var result = await Context!.PostJsonAsync<CreateStudentRequest, CreateStudentResponse>(
            "/api/students",
            new CreateStudentRequest(name, email));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null && _studentId == Guid.Empty)
            _studentId = result.Body.Id;
    }

    [When("I get all students")]
    public async Task GetAllStudents()
    {
        var result = await Context!.GetJsonAsync<List<Student>>("/api/students");
        _lastStatusCode = result.StatusCode;
        _students = result.Body ?? [];
    }

    [When("I get student by id {string}")]
    public async Task GetStudentByStringId(string id)
    {
        var result = await Context!.GetJsonAsync<Student>($"/api/students/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I get the student by id")]
    public async Task GetStudentById()
    {
        var result = await Context!.GetJsonAsync<Student>($"/api/students/{_studentId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I update the student name to {string}")]
    public async Task UpdateStudent(string newName)
    {
        // Use the POST alias for update so the spec text "update the student"
        // exercises a full round-trip without committing the spec to a verb;
        // the host endpoint accepts both POST and PUT at /api/students/{id}.
        var result = await Context!.PostJsonAsync<UpdateStudentRequest, Student>(
            $"/api/students/{_studentId}",
            new UpdateStudentRequest(newName));
        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the student")]
    public async Task DeleteStudent()
    {
        var result = await Context!.DeleteAsync($"/api/students/{_studentId}");
        _lastStatusCode = result.StatusCode;
    }

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Check("the student id is returned")]
    public bool StudentIdReturned() => _studentId != Guid.Empty;

    [Check("at least {int} students are returned")]
    public bool AtLeastNStudents(int min) => _students.Count >= min;

    [Check("they are ordered by name")]
    public bool OrderedByName()
    {
        var names = _students.Select(s => s.Name).ToList();
        return names.SequenceEqual(names.OrderBy(n => n));
    }
}
