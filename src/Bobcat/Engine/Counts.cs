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
            
            case ResultStatus.failed:
                Wrongs++;
                break;
            
            case ResultStatus.ok:
                return;

            case ResultStatus.error:
            case ResultStatus.missing:
            case ResultStatus.invalid:
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