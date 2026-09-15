using Bobcat.CritterStack;

namespace BankAccountES.Tests;

/// <summary>
/// Features/AccountView.feature — the sample's one View slice, written entirely in the shipped
/// CritterStack grammar so the fixture is the base class and nothing else (bobcat#104). It exists
/// for bobcat#300: a spec-declared View slice that must fold into the store-derived one by name.
/// </summary>
public class AccountViewFixture : CritterStackFixture;
