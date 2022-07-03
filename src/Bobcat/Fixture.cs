using System.Linq.Expressions;

namespace Bobcat;

public class Specification
{
    public static Specification operator +(Specification spec, Expression<Action> action) => spec;

}


public class Fixture
{
    public void Define(Expression<Func<object[]>> expression)
    {

    }
    
    public Specification Spec { get; protected set; }

    public class ReturnValue<T>
    {
        public void ToBe(T value)
        {
        }
    }

    public ReturnValue<T> Expect<T>(Expression<Func<T>> expression)
    {
        return new ReturnValue<T>();
    }

    public string NameIs()
    {
        return "foo";
    }



    public void Stuff()
    {
        Spec += () => NameIs().ShouldBe("foo");
    }
}

public static class Expectations
{
    public static void ShouldBe<T>(this T value, T expected)
    {
        
    }
}