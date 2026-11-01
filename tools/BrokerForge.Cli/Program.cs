using BrokerForge.Cli;

return args.FirstOrDefault() switch
{
    "validate" => ValidateCommand.Run(args[1..], Console.Out),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          bf validate <descriptorsRoot> --schemas <schemasRoot>
        """);
    return 2;
}
