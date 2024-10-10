namespace JKP.CloudflareDynamicIPUpdate.Serialization;
public enum Scope
{
    Universe = 0,
    /* User defined values  */
    Site = 200,
    Link = 253,
    Host = 254,
    Nowhere = 255
};