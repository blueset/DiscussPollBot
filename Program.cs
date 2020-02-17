﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PollBot {

    internal class Program {
        private static Config cfg;
        private static DB db;
        private static TelegramBotClient botClient;
        private static string botname;

        private static void Main(string[] _) {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
            cfg = deserializer.Deserialize<Config>(input: System.IO.File.ReadAllText("config.yaml"));
            db = new DB(cfg.Database);
            botClient = new TelegramBotClient(token: cfg.TelegramToken);
            var me = botClient.GetMeAsync().Result;
            botname = me.Username;
            Console.WriteLine($"UserID {me.Id} NAME: {me.Username}.");
            cfg.Admins = botClient.GetChatAdministratorsAsync(cfg.MainChatId).Result.Select(x => x.User.Id);
            botClient.StartReceiving(allowedUpdates: new UpdateType[] { UpdateType.Message, UpdateType.CallbackQuery });
            botClient.OnMessage += BotClient_OnMessage;
            botClient.OnCallbackQuery += BotClient_OnCallbackQuery;
            while (true) {
                Thread.Sleep(millisecondsTimeout: int.MaxValue);
            }
        }

        private static async void BotClient_OnMessage(object sender, MessageEventArgs e) {
            if (e.Message.Text != null) {
                if (cfg.DebugMode)
                    Console.WriteLine(ObjectDumper.Dump(e.Message));
                var text = e.Message.Text;
                var user = e.Message.From;
                if (text.StartsWith("/poll") || text.StartsWith("/mpoll")) {
                    HandleCreate(chat_id: e.Message.Chat.Id, user: user, text: e.Message.Text, msg: e.Message.MessageId);
                } else if (text == "/help" || text == $"/help@{botname}") {
                    await botClient.SendTextMessageAsync(e.Message.Chat.Id, cfg.translation.Help, replyToMessageId: e.Message.MessageId);
                } else if (text == "/stats" || text == $"/stats@{botname}") {
                    HandleStat(chat_id: e.Message.Chat.Id, e.Message.MessageId);
                } else if (text == "/refresh_admin" || text == $"/refresh_admin@{botname}") {
                    cfg.Admins = (await botClient.GetChatAdministratorsAsync(cfg.MainChatId)).Select(x => x.User.Id);
                } else if (text == "/dup" || text == $"/dup@{botname}") {
                    HandleDuplicate(msg: e.Message);
                }
            }
        }

        private static async void HandleDuplicate(Message msg) {
            if ((ChatId) msg.Chat.Id != cfg.MainChatId) {
                await botClient.SendTextMessageAsync(msg.Chat.Id, cfg.translation.DisallowError);
                return;
            }
            if (cfg.DeleteOrigin)
                await botClient.DeleteMessageAsync(cfg.MainChatId, msg.MessageId);
            var rep = msg.ReplyToMessage;
            if (rep == null) {
                await botClient.SendTextMessageAsync(cfg.MainChatId, cfg.translation.NoReplyError);
                return;
            }
            if (rep.Poll == null) {
                await botClient.SendTextMessageAsync(cfg.MainChatId, cfg.translation.NotPollError);
                return;
            }
            if (!rep.Poll.IsClosed) {
                await botClient.SendTextMessageAsync(cfg.MainChatId, cfg.translation.NotClosedError);
                return;
            }
            if (rep.Poll.Type == "quiz") {
                await botClient.SendTextMessageAsync(cfg.MainChatId, cfg.translation.NotQuizError);
                return;
            }
            await botClient.SendTextMessageAsync(cfg.MainChatId, cfg.translation.Duplicate,
                parseMode: ParseMode.Html,
                disableWebPagePreview: true,
                disableNotification: true,
                replyToMessageId: rep.MessageId,
                replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[] {
                    InlineKeyboardButton.WithCallbackData(cfg.translation.Approve, "duplicate"),
                    InlineKeyboardButton.WithCallbackData(cfg.translation.Reject, "reject") }));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>")]
        private static async void BotClient_OnCallbackQuery(object sender, CallbackQueryEventArgs e) {
            const string _approve = "approve ";
            const string _duplicate = "duplicate";
            var query = e.CallbackQuery;
            try {
                if (!cfg.Admins.Contains(query.From.Id)) {
                    await botClient.AnswerCallbackQueryAsync(query.Id, cfg.translation.PermissionError);
                    return;
                }
                if (query.Data.StartsWith(_approve)) {
                    var hash = int.Parse(query.Data.Remove(0, _approve.Length));
                    var origin = query.Message.ReplyToMessage;
                    var text = origin.Text;
                    var shash = text.GetHashCode();
                    if (!VerifyMessage(text, out var firstline, out var opts)) {
                        await botClient.DeleteMessageAsync(cfg.MainChatId, query.Message.MessageId);
                        await botClient.SendTextMessageAsync(origin.Chat.Id, cfg.translation.FormatError);
                        return;
                    }
                    if (hash != shash) {
                        await Task.WhenAll(
                            botClient.AnswerCallbackQueryAsync(query.Id, cfg.translation.HashMisMatchError),
                            botClient.DeleteMessageAsync(cfg.MainChatId, query.Message.MessageId),
                            SendRequest(origin.From, firstline, opts, origin.MessageId, shash));
                        return;
                    }
                    await SendPoll(origin.From, firstline, opts);
                    await Task.WhenAll(
                        botClient.DeleteMessageAsync(cfg.MainChatId, query.Message.MessageId),
                        botClient.AnswerCallbackQueryAsync(query.Id, cfg.translation.Approved));
                    if (cfg.DeleteOrigin)
                        await botClient.DeleteMessageAsync(cfg.MainChatId, origin.MessageId);
                } else if (query.Data == _duplicate) {
                    var origin = query.Message.ReplyToMessage;
                    await DuplicatePoll(origin);
                    await Task.WhenAll(
                        botClient.DeleteMessageAsync(cfg.MainChatId, query.Message.MessageId),
                        botClient.AnswerCallbackQueryAsync(query.Id, cfg.translation.Approved));
                    if (cfg.DeleteOrigin)
                        await botClient.DeleteMessageAsync(cfg.MainChatId, origin.MessageId);
                } else {
                    await Task.WhenAll(
                        botClient.SendTextMessageAsync(cfg.MainChatId, cfg.translation.RejectError, replyToMessageId: query.Message.ReplyToMessage.MessageId),
                        botClient.DeleteMessageAsync(cfg.MainChatId, query.Message.MessageId),
                        botClient.AnswerCallbackQueryAsync(query.Id, cfg.translation.Rejected));
                }
            } catch (Exception ex) {
                Console.WriteLine(ex);
                try {
                    await botClient.DeleteMessageAsync(query.Message.Chat.Id, query.Message.MessageId);
                } catch { }
                try {
                    await botClient.AnswerCallbackQueryAsync(query.Id, cfg.translation.ExceptionError);
                    await botClient.SendTextMessageAsync(cfg.MainChatId, cfg.translation.ExceptionError);
                } catch { }
            }
        }

        private static async void HandleCreate(ChatId chat_id, User user, string text, int msg) {
            try {
                var direct_send = cfg.Admins.Contains(user.Id) && cfg.DirectSend;
                if (chat_id != cfg.MainChatId && !direct_send) {
                    await botClient.SendTextMessageAsync(chat_id, cfg.translation.DisallowError);
                    return;
                }
                if (!VerifyMessage(text, out var firstline, out var opts)) {
                    await botClient.SendTextMessageAsync(chat_id, cfg.translation.FormatError);
                    return;
                }
                if (direct_send) {
                    await SendPoll(user, firstline, opts);
                } else {
                    await SendRequest(user, firstline, opts, msg, text.GetHashCode());
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.StackTrace);
                await botClient.SendTextMessageAsync(chat_id, cfg.translation.ExceptionError);
                return;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0018:Inline variable declaration", Justification = "<Pending>")]
        private static async Task SendRequest(User user, string firstline, IEnumerable<string> opts, int msg, int hash) {
            string title;
            bool multi;
            if (firstline.TryRemovePrefix("/poll ", out title) || firstline.TryRemovePrefix($"/poll@{botname} ", out title)) {
                multi = false;
            } else if (firstline.TryRemovePrefix("/mpoll ", out title) || firstline.TryRemovePrefix($"/mpoll@{botname} ", out title)) {
                multi = true;
            } else {
                Console.WriteLine($"Unexcepted request: {firstline}");
                return;
            }
            await botClient.SendPollAsync(cfg.MainChatId, $"{title} by {user.FirstName} {user.LastName}", opts,
                allowsMultipleAnswers: multi,
                isAnonymous: true,
                replyToMessageId: msg,
                isClosed: true,
                replyMarkup: new InlineKeyboardMarkup(new InlineKeyboardButton[] {
                    InlineKeyboardButton.WithCallbackData(cfg.translation.Approve, $"approve {hash}"),
                    InlineKeyboardButton.WithCallbackData(cfg.translation.Reject, "reject") }));
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0018:Inline variable declaration", Justification = "<Pending>")]
        private static async Task SendPoll(User user, string firstline, IEnumerable<string> opts) {
            string title;
            bool multi;
            if (firstline.TryRemovePrefix("/poll ", out title) || firstline.TryRemovePrefix($"/poll@{botname} ", out title)) {
                multi = false;
            } else if (firstline.TryRemovePrefix("/mpoll ", out title) || firstline.TryRemovePrefix($"/mpoll@{botname} ", out title)) {
                multi = true;
            } else {
                Console.WriteLine($"Unexcepted request: {firstline}");
                return;
            }
            var msg = await botClient.SendPollAsync(cfg.SendChatId, $"{title} by {user.FirstName} {user.LastName}", opts, allowsMultipleAnswers: multi, isAnonymous: true);
            db.AddLog(user.Id, user.Username, user.FirstName, user.LastName, title, msg.MessageId);
        }

        private static async Task DuplicatePoll(Message origin) {
            var poll = origin.Poll;
            var user = origin.From;
            var msg = await botClient.SendPollAsync(cfg.MainChatId, $"{poll.Question} by {user.FirstName} {user.LastName}",
                options: poll.Options.Select(op => op.Text),
                allowsMultipleAnswers: poll.AllowsMultipleAnswers,
                isAnonymous: true);
            db.AddLog(user.Id, user.Username, user.FirstName, user.LastName, poll.Question, msg.MessageId);
        }

        private static bool VerifyMessage(string data, out string firstline, out IEnumerable<string> opts) {
            var sp = data.Split("\n");
            firstline = sp[0].Trim();
            if (sp.Length > 2 && firstline.Split(' ', 2).Length == 2) {
                opts = sp.Skip(1).Select(x => x.Trim());
                return true;
            }
            firstline = null;
            opts = null;
            return false;
        }

        private static async void HandleStat(long chat_id, int msg_id) {
            var log = cfg.translation.Stats + "\n";
            foreach (var entry in db.StatLog()) {
                log += $"<b>{entry.Count}</b>: {entry.Name}\n";
            }
            await botClient.SendTextMessageAsync(chat_id, log, replyToMessageId: msg_id, disableWebPagePreview: true, parseMode: ParseMode.Html);
        }
    }
}