namespace Capivarinha

open System.Threading.Tasks
open System.Threading
open Discord
open Discord.WebSocket
open FSharp.Control
open Model
open FsToolkit.ErrorHandling.Operator.Result

module Main =
    let commandMailbox = MailboxProcessor.Start(fun inbox -> async {
        let! value = inbox.Receive()
        do! value |> Async.AwaitTask
    })

    let tryCmdHandler =
        let cmdHandler = Command.handleCommand commandMailbox.Post
        Command.tryCommandHandler cmdHandler

    [<EntryPoint>]
    let main _ =
        let settings = Settings.load ()
        let connectionString = Settings.databaseConnectionString settings

        Database.Infrastructure.migrate connectionString

        task {
            let config = DiscordSocketConfig(GatewayIntents=(GatewayIntents.AllUnprivileged ||| GatewayIntents.MessageContent))
            let client = new DiscordSocketClient(config)

            let tryUser user = Command.tryCommandUser client user

            let deps = {
                Logger = ()
                ConnectionString = connectionString
                Client = client
                Settings = settings
            }

            client.add_Ready (Client.onReady deps)

            client.add_SlashCommandExecuted (fun command -> task {
                tryUser command.User command
                |> Result.bind (Client.trySlashCommand)
                |> tryCmdHandler
            })

            client.add_ReactionAdded (fun message _channel reaction -> task {
                let! msg = message.GetOrDownloadAsync()
                let! reactionUser = deps.Client.GetUserAsync(reaction.User.Value.Id)

                tryUser reactionUser reaction.Emote
                |> Result.bind (Client.tryReactionAdded reaction reactionUser msg)
                |> tryCmdHandler
            })

            client.add_MessageUpdated (fun _ message _ -> task {
                tryUser message.Author message
                |> Result.bind Client.tryMessageReceived
                |> tryCmdHandler
            })

            client.add_MessageReceived (fun message -> task {
                tryUser message.Author message
                |> Result.bind Client.tryMessageReceived
                |> tryCmdHandler
            })

            do! client.LoginAsync(TokenType.Bot, settings.DiscordToken)
            do! client.StartAsync()

            do! Task.Delay(Timeout.Infinite)
        }
        |> Async.AwaitTask
        |> Async.RunSynchronously

        0
