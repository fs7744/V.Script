namespace V.Script.Diagnostics;

/// <summary>
/// Stable identifiers for every diagnostic the engine can produce.
/// <list type="bullet">
///   <item>1xxx — lexical and syntactic</item>
///   <item>2xxx — binding and type system</item>
///   <item>3xxx — async and control flow restrictions</item>
///   <item>9xxx — unsupported language constructs</item>
/// </list>
/// </summary>
public enum ErrorCode
{
    None = 0,

    // ---- 1xxx lexical / syntactic ----
    UnexpectedCharacter = 1001,
    UnterminatedString = 1002,
    UnterminatedComment = 1003,
    InvalidNumericLiteral = 1004,
    InvalidEscapeSequence = 1005,
    UnexpectedToken = 1010,
    ExpectedToken = 1011,
    ExpectedExpression = 1012,
    ExpectedStatement = 1013,
    ExpectedIdentifier = 1014,
    ExpectedPattern = 1015,

    // ---- 2xxx binding / types ----
    UndefinedName = 2001,
    UndefinedMember = 2002,
    NoMatchingOverload = 2003,
    AmbiguousOverload = 2004,
    CannotConvert = 2005,
    CannotConvertImplicitly = 2006,
    OperatorNotDefined = 2007,
    NotAssignable = 2008,
    VariableAlreadyDefined = 2009,
    UnknownType = 2010,
    NotInvocable = 2011,
    NotIndexable = 2012,
    NotEnumerable = 2013,
    WrongArgumentCount = 2015,
    ReturnTypeMismatch = 2016,
    CannotInferType = 2017,
    NotAWaitable = 2018,
    ConditionMustBeBool = 2019,
    PatternNeverMatches = 2025,
    SwitchArmTypeMismatch = 2026,
    MemberIsNotStatic = 2020,
    MemberIsStatic = 2021,
    PropertyHasNoGetter = 2022,
    PropertyHasNoSetter = 2023,

    // ---- 3xxx async / control flow ----
    AwaitInSynchronousScript = 3001,
    BreakOutsideLoop = 3002,
    ContinueOutsideLoop = 3003,
    AwaitInExceptionHandler = 3004,
    NotAllCodePathsReturn = 3005,
    SwitchSectionFallsThrough = 3006,

    // ---- 9xxx not implemented ----
    GenericMethodInferenceNotSupported = 9002,
    AwaitInLambda = 9006,
    ConstructNotSupported = 9010,
}

public static class ErrorCodeExtensions
{
    /// <summary>Renders the code in its canonical wire form, e.g. <c>VS3004</c>.</summary>
    public static string Code(this ErrorCode code) => $"VS{(int)code:D4}";
}
