if (args.Length != 1) throw new ArgumentException("Supply the absolute JSON output path.");
await Penghou.Hufu.Biscuit.Tests.BiscuitBudgetProbe.RunAsync(args[0]);
