namespace Bobcat.Engine;

public record Counts
{
    public Counts()
    {
    }

    public Counts(int rights, int wrongs, int errors)
    {
        Rights = rights;
        Wrongs = wrongs;
        Errors = errors;
    }

    public int Rights { get; set; }
    public int Wrongs { get; set; }
    public int Errors { get; set; }

    public void Read(ResultStatus status)
    {
        switch (status)
        {
            case ResultStatus.success:
                Rights++;
                break;
            
            // A MISSING or EXTRA set-verification row, or an expected cell naming nothing on the subject, is
            // a disagreement with the specification -- a wrong, as the step's own status already says
            // (ScenarioRecorder: failed/missing/invalid => failed). Only a thrown exception is an error.
            case ResultStatus.failed:
            case ResultStatus.missing:
            case ResultStatus.invalid:
                Wrongs++;
                break;
            
            case ResultStatus.ok:
                return;

            case ResultStatus.error:
            default:
                Errors++;
                break;
        }
    }

    public bool Succeeded => (Wrongs + Errors) == 0;

    public virtual bool Equals(Counts? other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Rights == other.Rights && Wrongs == other.Wrongs && Errors == other.Errors;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Rights, Wrongs, Errors);
    }

    public override string ToString()
    {
        var prefix = Succeeded ? "Succeeded" : "Failed";
        return $"{prefix} with {nameof(Rights)}: {Rights}, {nameof(Wrongs)}: {Wrongs}, {nameof(Errors)}: {Errors}";
    }
}