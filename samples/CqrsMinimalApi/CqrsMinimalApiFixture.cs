using Bobcat;
using Bobcat.Alba;

namespace CqrsMinimalApi.Tests;

[FixtureTitle("CQRS Minimal API Students")]
public class CqrsMinimalApiFixture
{
    private Guid _studentId;
    private int _lastStatusCode;
    private List<StudentDto> _students = [];

    [When("I create a student with name {string} and email {string}")]
    [Given("I create a student with name {string} and email {string}")]
    public async Task CreateStudent(IStepContext context, string name, string email)
    {
        var result = await context.PostJsonAsync<CreateStudentRequest, CreateStudentResponse>(
            "/api/students",
            new CreateStudentRequest(name, email));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null && _studentId == Guid.Empty)
            _studentId = result.Body.Id;
    }

    [When("I get all students")]
    public async Task GetAllStudents(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<StudentDto>>("/api/students");
        _lastStatusCode = result.StatusCode;
        _students = result.Body ?? [];
    }

    [When("I get student by id {string}")]
    public async Task GetStudentByStringId(IStepContext context, string id)
    {
        var result = await context.GetJsonAsync<StudentDto>($"/api/students/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I get the student by id")]
    public async Task GetStudentById(IStepContext context)
    {
        var result = await context.GetJsonAsync<StudentDto>($"/api/students/{_studentId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I update the student name to {string}")]
    public async Task UpdateStudent(IStepContext context, string newName)
    {
        var result = await context.PostJsonAsync<UpdateStudentRequest, object>(
            $"/api/students/{_studentId}",
            new UpdateStudentRequest(_studentId, newName));
        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the student")]
    public async Task DeleteStudent(IStepContext context)
    {
        var result = await context.DeleteAsync($"/api/students/{_studentId}");
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the student id is returned")]
    [Check]
    public bool StudentIdReturned() => _studentId != Guid.Empty;

    [Then("at least {int} students are returned")]
    [Check]
    public bool AtLeastNStudents(int min) => _students.Count >= min;

    [Then("they are ordered by name")]
    [Check]
    public bool OrderedByName()
    {
        var names = _students.Select(s => s.Name).ToList();
        return names.SequenceEqual(names.OrderBy(n => n));
    }
}

record CreateStudentRequest(string Name, string Email);
record CreateStudentResponse(Guid Id);
record UpdateStudentRequest(Guid Id, string Name);
record StudentDto(Guid Id, string Name, string Email);
