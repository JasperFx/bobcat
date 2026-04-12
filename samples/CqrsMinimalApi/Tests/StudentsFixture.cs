using Alba;
using Bobcat;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using CqrsMinimalApi;
using Shouldly;

namespace CqrsMinimalApi.Tests;

[FixtureTitle("CQRS Minimal API Students")]
public class StudentsFixture : Fixture
{
    private IAlbaHost _host = null!;

    private Student? _student;
    private Student? _updatedStudent;
    private Student[]? _allStudents;
    private int _lastStatusCode;
    private long _lastCheckedId;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _student = null;
        _updatedStudent = null;
        _allStudents = null;
        _lastStatusCode = 0;
        _lastCheckedId = 0;
        return Task.CompletedTask;
    }

    // ── Given ────────────────────────────────────────────────────────────────

    [Given("a student named {string} exists")]
    public async Task CreateStudentGiven(string name)
    {
        using var session = _host.DocumentStore().LightweightSession();
        session.Store(new Student { Name = name });
        await session.SaveChangesAsync();
    }

    [Given("a student named {string} with email {string} exists")]
    public async Task CreateStudentWithEmailGiven(string name, string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateStudentRequest(name, null, email, null)).ToUrl("/student/create");
            x.StatusCodeShouldBeOk();
        });
        _student = result.ReadAsJson<Student>()!;
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I create a student named {string} with address {string} and email {string}")]
    public async Task CreateStudent(string name, string address, string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateStudentRequest(name, address, email, new DateTime(2000, 1, 1))).ToUrl("/student/create");
            x.StatusCodeShouldBeOk();
        });
        _student = result.ReadAsJson<Student>()!;
        _lastStatusCode = 200;
    }

    [When("I get all students")]
    public async Task GetAllStudents()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/student/get-all");
            x.StatusCodeShouldBeOk();
        });
        _allStudents = result.ReadAsJson<Student[]>()!;
    }

    [When("I get a student by id {int}")]
    public async Task GetStudentById(int id)
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/student/get-by-id?id={id}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get the student by its id")]
    public async Task GetStudentByItsId()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/student/get-by-id?id={_student!.Id}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I update the student name to {string} and email to {string}")]
    public async Task UpdateStudent(string name, string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateStudentRequest(name, null, email, null)).ToUrl($"/student/update/{_student!.Id}");
            x.StatusCodeShouldBeOk();
        });
        _updatedStudent = result.ReadAsJson<Student>()!;
        _lastStatusCode = 200;
    }

    [When("I delete the student")]
    public async Task DeleteStudent()
    {
        await _host.Scenario(x =>
        {
            x.Delete.Url($"/student/delete?id={_student!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Then("the student name should be {string}")]
    public void StudentNameShouldBe(string expected) => _student!.Name.ShouldBe(expected);

    [Check("the student id should be valid")]
    public bool StudentIdIsValid() => _student?.Id > 0;

    [Then("there should be at least {int} students")]
    public void StudentCountAtLeast(int min) => (_allStudents!.Length >= min).ShouldBeTrue();

    [Then("the first student should be {string}")]
    public void FirstStudentShouldBe(string expected) => _allStudents![0].Name.ShouldBe(expected);

    [Then("the updated student name should be {string}")]
    public void UpdatedStudentNameShouldBe(string expected) => _updatedStudent!.Name.ShouldBe(expected);

    [Then("the updated student email should be {string}")]
    public void UpdatedStudentEmailShouldBe(string expected) => _updatedStudent!.Email.ShouldBe(expected);
}
