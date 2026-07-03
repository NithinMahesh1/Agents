using Agents.Cli;

// Thin entry point: all logic lives in CliApp and the command classes. The return value becomes the
// process exit code (0 = success, 2 = ran but did not complete the goal, 130 = cancelled, 1 = error).
return await CliApp.RunAsync(args);
