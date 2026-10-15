namespace mEmuera.Contracts;

public static class NameQuerySupport
{
    public static bool IsCharacterTable(string name) => name is "NAME" or "CALLNAME" or "RELATION";

    public static bool HasSystemTable(string name) => IsCharacterTable(name) || SystemTable(name) is not null;

    public static string? SystemTable(string name) => name switch
    {
        "MAXBASE" or "DOWNBASE" or "LOSEBASE" => "BASENAME",
        "UP" or "DOWN" or "JUEL" or "GOTJUEL" or "CUP" or "CDOWN" => "PALAMNAME",
        "ITEMSALES" or "ITEMPRICE" => "ITEMNAME",
        "NOWEX" => "EXNAME",
        "STR" => "STRNAME",
        "TRAINNAME" or "CDFLAGNAME1" or "CDFLAGNAME2" => name,
        "ABL" or "EXP" or "TALENT" or "PALAM" or "MARK" or "ITEM" or "BASE" or "SOURCE" or "EX"
            or "EQUIP" or "TEQUIP" or "FLAG" or "TFLAG" or "CFLAG" or "TCVAR" or "CSTR" or "STAIN"
            or "TSTR" or "SAVESTR" or "GLOBAL" or "GLOBALS" or "DAY" or "TIME" or "MONEY" => name + "NAME",
        _ => null
    };
}
