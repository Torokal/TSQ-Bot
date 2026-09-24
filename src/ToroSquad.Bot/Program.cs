using ToroSquad.Bot;

// ToroSquad Bot — single executable. Verbs (see README.md / scripts/*.ps1):
//   run (default)                       start the bot
//   commands export [--out FILE]        build + validate the slash-command manifest offline
//   commands sync --guild ID|--global [--apply] [--prune]   dry-run by default
//   doctor                              local configuration diagnosis (no secret values printed)
//   db migrate | db backup [--out DIR] | db restore FILE --yes
//   simulate                            offline end-to-end run on fixture data (temp database, fake Discord)
return await Cli.RunAsync(args);
