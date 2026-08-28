using Bobcat.CritterStack;

namespace BankAccountES.Tests;

/// <summary>
/// The whole vocabulary for Features/FreezeAccount.feature comes from the shipped CritterStack
/// grammar — deriving from <see cref="CritterStackFixture"/> is the entire fixture (bobcat#104).
/// Contrast with <see cref="BankAccountESFixture"/>, which drives the HTTP surface through Alba;
/// this one dispatches over the Wolverine bus, which is what the grammar's typed steps do.
/// </summary>
public class FreezeAccountFixture : CritterStackFixture;
