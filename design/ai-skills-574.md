# ai-skills entry for bobcat#574

C16 of `design/bdd-tools-comparison.md` (decided 2026-10-16): practice guidance for agents in
`critterstack-sdd-bobcat-authoring`. **No code.** The docs half is the strategy tutorial, #558.

This file holds the section **exactly as it goes into ai-skills**, because the session that wrote it
was bounded to this repository. Whoever opens the ai-skills PR pastes the block below into
`skills/critterstack-sdd-bobcat-authoring/SKILL.md`, between `## A specification` and
`## Wrongs are gathered, errors end the test` (it follows the anatomy of one spec, and comes before
the material on what a run reports), then follows ai-skills' own `CLAUDE.md`: a fragment in
`docs/release-notes/<version>.md` and `npm run docs:generate`. The `description:` gains
"where a spec belongs (store, message, HTTP or UI) and the driver pattern for UI specs", because an
agent asked "should this be a Playwright test?" should find this skill.

## Decisions the text encodes

- **"One behaviour per spec" is deliberately absent (C12, dropped).** Sagas, process managers and
  workflow specs legitimately act again after asserting. The skill's existing "Act once" bullet
  is about *which* act the `Then…` steps judge, not a shape rule, and is left alone.
- **No API that does not exist yet.** C2's `SpecAttachment` / `IFailureCapture` (screenshot on a
  failed step) is decided for 1.0 but unbuilt, so the driver section says nothing about
  screenshots. Add a line when C2 ships.
- **The layers are the base classes**, named the way the "Pick a base class" table names them, so
  an agent maps the advice straight onto a type: `CritterStackSpec` → `WolverineSpec` →
  `WolverineHttpSpec` → a UI driver.

## The section

````markdown
## Where a spec belongs, and how its steps read

Three practices the vocabulary is built for. None is enforced; a spec that ignores them still runs,
but it is slower, more brittle and says less.

**Prove the behaviour at the lowest layer that can.** Each layer up adds a host, a transport or a
browser between the spec and the rule it is about, and each is a new way to fail for a reason
unrelated to the rule.

| The behaviour is… | Write it on | Because |
|---|---|---|
| a decision over events, an aggregate, a projection, a read model | `CritterStackSpec` (store only) | no bus, no HTTP; the fastest spec there is |
| what a handler does with a message: events, refusals, cascades, retries, scheduling | `WolverineSpec` | the tracked session is the only thing that sees messages |
| the HTTP contract: route, status code, ProblemDetails, binding, auth | `WolverineHttpSpec` | that is the only layer the contract exists at |
| what a person sees or does in a browser: layout, navigation, a client-side flow | Playwright, through a driver (below) | only the UI is UI |

- A business rule proved through the UI is proved at the wrong layer: it fails when a selector
  changes, and it runs the whole stack to check one `if`. Prove the rule once, low, and let the UI
  spec assume it.
- An HTTP endpoint that only forwards to a handler needs one `WolverineHttpSpec` for the contract;
  its refusals and edge cases belong on `WolverineSpec`, where they run without Alba.
- Going up a layer is right when the layer *is* the claim: a 400 with a ProblemDetails body, an
  authorization rule, a message that must reach another endpoint.

**Steps are declarative: what is true, not how to make it true.** A spec reads as the business
rule; the mechanics live in the step's method body.

```csharp
// Imperative: the reader has to execute it to know what it says
await GivenEvents<Account>(id, new AccountOpened(id, ownerId, "USD"));
await WhenPosted(new DepositFunds(id, 100), "/api/accounts/{id}/deposits", id);
await WhenPosted(new DepositFunds(id, 50), "/api/accounts/{id}/deposits", id);
await WhenPosted(new WithdrawFunds(id, 200), "/api/accounts/{id}/withdrawals", id);

// Declarative: arrange the state, act once, in the language of the domain
await GivenEvents<Account>(id,
    new AccountOpened(id, ownerId, "USD"),
    new FundsDeposited(id, 150, 150));
await WhenPosted(new WithdrawFunds(id, 200), "/api/accounts/{id}/withdrawals", id);
```

- Arrange with **events, the state the act starts from**, not with the commands that would have
  produced it. Arranging commands is allowed (the last command is the act), but every one is
  another handler the spec now depends on.
- Name your own step helpers after the business fact (`[Given("a customer with an overdue
  invoice")]`), not the mechanism (`[Given("insert a row into invoices")]`). The step text is what
  the rendered spec, the model and the next reader see.
- Values: assert with `Specify<T>()` on the members the rule is about, not a whole object that pins
  every incidental field.

**UI specs go through a driver.** A Playwright spec never touches the page. Page mechanics
(selectors, waits, clicks, frames) live in a driver class the spec holds; the spec calls `[Given]` /
`[When]` / `[Then]` helpers named in business language, and those helpers call the driver.

```csharp
public class CheckoutDriver(IPage page)          // mechanics only; no assertions about the business
{
    public async Task AddToCart(string sku)
    {
        await page.GetByTestId($"product-{sku}").GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
        await page.GetByTestId("cart-count").WaitForAsync();
    }

    public Task<string> CartTotal() => page.GetByTestId("cart-total").InnerTextAsync();
}

// In the spec class (or its base), helpers in the language of the business
[When("the shopper adds {sku} to the cart")]
public Task AddToCart(string sku) => _checkout.AddToCart(sku);

[Then("the cart total is {total}")]
public async Task CartTotalIs(string total) => SpecAssert.Check("cart total", await _checkout.CartTotal(), total);
```

- A selector appears in **one** place, so a UI change is one edit in the driver, never a sweep
  through specs.
- The helpers are step methods, so they render as steps and record their checks; a bare driver call
  from a test body renders nothing.
- Arrange a UI spec through the lower layers (`GivenEvents`, a seeded read model), not by clicking
  through the screens that would have created the state. Only the behaviour under test goes through
  the browser.
````
