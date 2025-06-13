using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Generic;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace CarInsuranceBot
{
    class Program
    {
        private const string TelegramBotToken = "TelegramBotToken";
        private const string MindeeApiKey = "MindeeApiKey";
        private const string OpenAiApiKey = "OpenAiApiKey";

        private static readonly ITelegramBotClient Bot = new TelegramBotClient(TelegramBotToken);
        private static readonly HttpClient MindeeClient = new HttpClient();
        private static readonly HttpClient OpenAIClient = new HttpClient();
        private static readonly Dictionary<long, UserState> UserStates = new Dictionary<long, UserState>();

        private class UserState
        {
            public string Stage { get; set; } = "Start";
            public string PassportPhotoId { get; set; }
            public string VehicleDocPhotoId { get; set; }
            public Dictionary<string, string> ExtractedData { get; set; }
        }

        static Program()
        {
            Console.WriteLine("Ініціалізація бота...");
            MindeeClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {MindeeApiKey}");
            OpenAIClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {OpenAiApiKey}");
            Console.WriteLine("Ініціалізація завершена успішно.");
        }

        static async Task Main()
        {
            var cts = new CancellationTokenSource();
            var receiverOptions = new ReceiverOptions { AllowedUpdates = { } };
            Bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cts.Token);
            Console.WriteLine("Бот запущено...");
            await Task.Delay(-1);
        }

        static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message == null) return;

            var chatId = update.Message.Chat.Id;
            var messageText = update.Message.Text?.ToLower() ?? "";
            UserState state = UserStates.ContainsKey(chatId) ? UserStates[chatId] : new UserState();

            Console.WriteLine($"Отримано повідомлення від {chatId}: {messageText}");
            switch (state.Stage)
            {
                case "Start":
                    if (messageText == "/start")
                    {
                        await HandleStartAsync(botClient, chatId, state, cancellationToken);
                    }
                    else
                    {
                        await botClient.SendTextMessageAsync(chatId, "Для початку введіть /start", cancellationToken: cancellationToken);
                    }
                    break;
                case "AwaitingPassport":
                    await HandlePassportPhotoAsync(botClient, update, chatId, state, cancellationToken);
                    break;
                case "AwaitingVehicleDoc":
                    await HandleVehicleDocPhotoAsync(botClient, update, chatId, state, cancellationToken);
                    break;
                case "ConfirmData":
                    await HandleDataConfirmationAsync(botClient, update, chatId, state, cancellationToken);
                    break;
                case "ConfirmPrice":
                    await HandlePriceConfirmationAsync(botClient, update, chatId, state, cancellationToken);
                    break;
            }

            UserStates[chatId] = state;
        }

        static async Task HandleStartAsync(ITelegramBotClient botClient, long chatId, UserState state, 
            CancellationToken cancellationToken)
        {
            var message = await CallOpenAIAsync("Привіт! Я твій помічник зі страхування авто. Я допоможу тобі оформити страховий поліс. Надішли фото свого паспорта для початку.", maxRetries: 2);
            await botClient.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
            state.Stage = "AwaitingPassport";
            Console.WriteLine($"Користувач {chatId} перейшов до етапу AwaitingPassport");
        }

        static async Task HandlePassportPhotoAsync(ITelegramBotClient botClient, Update update, long chatId, UserState state, 
            CancellationToken cancellationToken)
        {
            if (update.Message.Photo != null && update.Message.Photo.Length > 0)
            {
                state.PassportPhotoId = update.Message.Photo[^1].FileId;
                var message = await CallOpenAIAsync("Дякую за фото паспорта. Тепер надішли фото технічного паспорта автомобіля.", 
                    maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
                state.Stage = "AwaitingVehicleDoc";
                Console.WriteLine($"Користувач {chatId} надіслав паспорт, перейшов до AwaitingVehicleDoc");
            }
            else
            {
                var message = await CallOpenAIAsync("Надішли коректне фото паспорта.", maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
                Console.WriteLine($"Користувач {chatId} надіслав некоректне фото паспорта");
            }
        }

        static async Task HandleVehicleDocPhotoAsync(ITelegramBotClient botClient, Update update, long chatId, UserState state,
            CancellationToken cancellationToken)
        {
            if (update.Message.Photo != null && update.Message.Photo.Length > 0)
            {
                state.VehicleDocPhotoId = update.Message.Photo[^1].FileId;
                try
                {
                    state.ExtractedData = await CallMindeeApiAsync(state.PassportPhotoId, state.VehicleDocPhotoId);
                    var dataMessage = $"Підтвердь дані:\n" +
                        $"Ім'я: {state.ExtractedData["Name"]}\n" +
                        $"Номер паспорта: {state.ExtractedData["PassportNumber"]}\n" +
                        $"VIN авто: {state.ExtractedData["VehicleId"]}\n" +
                        $"Номерний знак: {state.ExtractedData["LicensePlate"]}\n" +
                        $"Марка авто: {state.ExtractedData["VehicleMake"]}\n" +
                        $"Рік випуску: {state.ExtractedData["Year"]}\n\n" +
                        "Відповідай 'Так' або 'Ні'.";
                    var message = await CallOpenAIAsync(dataMessage, maxRetries: 2);
                    await botClient.SendTextMessageAsync(chatId, message, replyMarkup: GetConfirmationKeyboard(), 
                        cancellationToken: cancellationToken);
                    state.Stage = "ConfirmData";
                    Console.WriteLine($"Користувач {chatId} надіслав техпаспорт, дані витягнуто, перейшов до ConfirmData");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    state.ExtractedData = new Dictionary<string, string>
                    {
                        { "Name", "Константин Константинопольский" },
                        { "PassportNumber", "АА111111" },
                        { "VehicleId", "A1231A123A123A123" },
                        { "LicensePlate", "АА 1111 АА" },
                        { "VehicleMake", "Audi A6" },
                        { "Year", "2016" }
                    };
                    var dataMessage = $"Підтвердь дані (симуляція через помилку API):\n" +
                        $"Ім'я: {state.ExtractedData["Name"]}\n" +
                        $"Номер паспорта: {state.ExtractedData["PassportNumber"]}\n" +
                        $"VIN авто: {state.ExtractedData["VehicleId"]}\n" +
                        $"Номерний знак: {state.ExtractedData["LicensePlate"]}\n" +
                        $"Марка авто: {state.ExtractedData["VehicleMake"]}\n" +
                        $"Рік випуску: {state.ExtractedData["Year"]}\n\n" +
                        "Відповідай 'Так' або 'Ні'.";
                    var message = await CallOpenAIAsync(dataMessage, maxRetries: 2);
                    await botClient.SendTextMessageAsync(chatId, message, replyMarkup: GetConfirmationKeyboard(),
                        cancellationToken: cancellationToken);
                    state.Stage = "ConfirmData";
                    Console.WriteLine($"Користувач {chatId} отримав помилку 404 від Mindee, використано симуляцію");
                }
            }
            else
            {
                var message = await CallOpenAIAsync("Надішли коректне фото технічного паспорта.", maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
                Console.WriteLine($"Користувач {chatId} надіслав некоректне фото техпаспорта");
            }
        }

        static async Task HandleDataConfirmationAsync(ITelegramBotClient botClient, Update update, long chatId,
            UserState state, CancellationToken cancellationToken)
        {
            var response = update.Message?.Text?.ToLower();
            if (response == "так")
            {
                var message = await CallOpenAIAsync("Вартість поліса — 100 USD. Згоден? Відповідай 'Так' або 'Ні'.", maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, replyMarkup: GetConfirmationKeyboard(), 
                    cancellationToken: cancellationToken);
                state.Stage = "ConfirmPrice";
                Console.WriteLine($"Користувач {chatId} підтвердив дані, перейшов до ConfirmPrice");
            }
            else if (response == "ні")
            {
                var message = await CallOpenAIAsync("Надішли ще раз фото паспорта та технічного паспорта.", maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
                state.PassportPhotoId = null;
                state.VehicleDocPhotoId = null;
                state.Stage = "AwaitingPassport";
                Console.WriteLine($"Користувач {chatId} відхилив дані, повернувся до AwaitingPassport");
            }
            else
            {
                var message = await CallOpenAIAsync("Відповідай 'Так' або 'Ні'.", maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, replyMarkup: GetConfirmationKeyboard(), 
                    cancellationToken: cancellationToken);
                Console.WriteLine($"Користувач {chatId} надіслав некоректну відповідь");
            }
        }

        static async Task HandlePriceConfirmationAsync(ITelegramBotClient botClient, Update update, long chatId, 
            UserState state, CancellationToken cancellationToken)
        {
            var response = update.Message?.Text?.ToLower();
            if (response == "так")
            {
                var policy = await GenerateInsurancePolicyAsync(state.ExtractedData);
                await botClient.SendDocumentAsync(chatId, InputFile.FromStream(new MemoryStream(Encoding.UTF8.GetBytes(policy)), 
                    "InsurancePolicy.txt"), cancellationToken: cancellationToken);
                var message = await CallOpenAIAsync("Вітаю! Твій поліс оформлено та надіслано. Дякую за покупку!", maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken);
                state.Stage = "Start";
                Console.WriteLine($"Користувач {chatId} підтвердив ціну, поліс надіслано, повернувся до Start");
            }
            else if (response == "ні")
            {
                var message = await CallOpenAIAsync("Вибач, але 100 USD — єдина ціна. Продовжуємо? Відповідай 'Так' або 'Ні'.", 
                    maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, replyMarkup: GetConfirmationKeyboard(), 
                    cancellationToken: cancellationToken);
                Console.WriteLine($"Користувач {chatId} відхилив ціну");
            }
            else
            {
                var message = await CallOpenAIAsync("Відповідай 'Так' або 'Ні'.", maxRetries: 2);
                await botClient.SendTextMessageAsync(chatId, message, replyMarkup: GetConfirmationKeyboard(),
                    cancellationToken: cancellationToken);
                Console.WriteLine($"Користувач {chatId} надіслав некоректну відповідь");
            }
        }

        static async Task<Dictionary<string, string>> CallMindeeApiAsync(string passportPhotoId, string vehicleDocPhotoId)
        {
            try
            {
                Console.WriteLine("Виклик Mindee API...");
                var passportFile = await Bot.GetFileAsync(passportPhotoId);
                var vehicleFile = await Bot.GetFileAsync(vehicleDocPhotoId);
                var passportStream = new MemoryStream();
                var vehicleStream = new MemoryStream();
                await Bot.DownloadFileAsync(passportFile.FilePath, passportStream);
                await Bot.DownloadFileAsync(vehicleFile.FilePath, vehicleStream);
                passportStream.Position = 0;
                vehicleStream.Position = 0;

                var passportForm = new MultipartFormDataContent
                {
                    { new StreamContent(passportStream), "document", "passport.jpg" }
                };
                var vehicleForm = new MultipartFormDataContent
                {
                    { new StreamContent(vehicleStream), "document", "vehicle.jpg" }
                };

                var passportResponse = await MindeeClient.PostAsync("https://api.mindee.net/v1/products/passports/v1/predict", 
                    passportForm);
                passportResponse.EnsureSuccessStatusCode();
                var passportJson = await passportResponse.Content.ReadAsStringAsync();
                var passportData = JsonNode.Parse(passportJson);
                Console.WriteLine("Дані паспорта отримано.");

                var vehicleResponse = await MindeeClient.PostAsync("https://api.mindee.net/v1/products/international_id/v1/predict",
                    vehicleForm);
                vehicleResponse.EnsureSuccessStatusCode();
                var vehicleJson = await vehicleResponse.Content.ReadAsStringAsync();
                var vehicleData = JsonNode.Parse(vehicleJson);
                Console.WriteLine("Дані техпаспорта отримано.");

                var name = passportData?["document"]?["inference"]?["prediction"]?["given_names"]?[0]?["value"]?.GetValue<string>() ?? "Unknown";
                var passportNumber = passportData?["document"]?["inference"]?["prediction"]?["id_number"]?["value"]?.GetValue<string>() ?? "Unknown";
                var vehicleId = vehicleData?["document"]?["inference"]?["prediction"]?["id_number"]?["value"]?.GetValue<string>() ?? "Unknown";
                var licensePlate = vehicleData?["document"]?["inference"]?["prediction"]?["license_plate"]?["value"]?.GetValue<string>() ?? "Unknown";
                var vehicleMake = vehicleData?["document"]?["inference"]?["prediction"]?["make"]?["value"]?.GetValue<string>() ?? "Unknown";
                var year = vehicleData?["document"]?["inference"]?["prediction"]?["year"]?["value"]?.GetValue<string>() ?? "Unknown";

                return new Dictionary<string, string>
                {
                    { "Name", name },
                    { "PassportNumber", passportNumber },
                    { "VehicleId", vehicleId },
                    { "LicensePlate", licensePlate },
                    { "VehicleMake", vehicleMake },
                    { "Year", year }
                };
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new Dictionary<string, string>
                {
                    { "Name", "Константин Константинопольский" },
                    { "PassportNumber", "АА111111" },
                    { "VehicleId", "A1231A123A123A123" },
                    { "LicensePlate", "АА 1111 АА" },
                    { "VehicleMake", "Audi A6" },
                    { "Year", "2016" }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка в CallMindeeApi: {ex.Message}");
                throw;
            }
        }

        static async Task<string> CallOpenAIAsync(string input, int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Console.WriteLine($"Виклик OpenAI API (спроба {attempt})...");
                    var requestBody = new
                    {
                        model = "gpt-3.5-turbo-instruct",
                        messages = new[] { new { role = "user", content = input } },
                        max_tokens = 500
                    };
                    var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                    var response = await OpenAIClient.PostAsync("https://api.openai.com/v1/completions", content);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonNode.Parse(json);
                    Console.WriteLine("Відповідь OpenAI отримано.");
                    return data?["choices"]?[0]?["text"]?.GetValue<string>() ?? input;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    Console.WriteLine($"Помилка 429 в CallOpenAI (спроба {attempt}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(2000 * attempt);
                        continue;
                    }
                    Console.WriteLine("Вичерпано спроби для OpenAI.");
                    return input;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Помилка в CallOpenAI: {ex.Message}");
                    return input;
                }
            }
            return input;
        }

        static async Task<string> GenerateInsurancePolicyAsync(Dictionary<string, string> data)
        {
            var policyNumber = "111111111";
            var startDate = DateTime.Now.ToString("dd.MM.yyyy");
            var endDate = DateTime.Now.AddYears(1).ToString("dd.MM.yyyy");
            var policyText = $@"Поліс №{policyNumber}
обов'язкового страхування цивільно-правової відповідальності власників наземних транспортних засобів

Цей документ є візуальною формою полісу, що посвідчує укладення внутрішнього електронного договору
обов’язкового страхування цивільно-правової відповідальності власників наземних транспортних засобів.
Договір діє виключно на території України на умовах, встановлених Законом України «Про обов’язкове страхування цивільно-правової відповідальності власників наземних транспортних засобів».

Страховик
«Назва страхової компанії»
Адреса: Україна, м. Київ, вул. Хрещатик
Телефон: 0 800 000 000

Страхувальник
{data["Name"]}
Адреса: Київ, Хрещатик, 1, 1
Дата народження: 01.01.1990
Паспорт: {data["PassportNumber"]}
Виданий: Дарницьким РУ ГУ МВС України в м. Києві, 01.01.2010

Строк дії Договору
З 00:00 {startDate} по {endDate} включно
Договір набирає чинності з початку строку його дії, але не раніше дати його реєстрації у єдиній централізованій базі даних.

Дата реєстрації Договору: 00:00 {startDate}

Страхова сума на одного
За шкоду, заподіяну життю і здоров'ю: двісті тисяч гривень 00 коп.
За шкоду, заподіяну майну: сто тисяч гривень 00 коп.

Розмір франшизи
Одна тисяча гривень 00 коп.

Забезпечений транспортний засіб
Марка: {data["VehicleMake"]}
Тип: A2
Номерний знак: {data["LicensePlate"]}
VIN: {data["VehicleId"]}
Рік випуску: {data["Year"]}
Місце реєстрації: Київ

Особливі умови використання ТЗ:
ТЗ використовується як таксі/маршрутне таксі: НІ
ТЗ підлягає обов'язковому технічному контролю: НІ
До керування допущені особи з водійським стажем менше 3-х років: ТАК
ТЗ використовується протягом повного строку страхування

Коефіцієнти
БП: 180, К1: 0.68, К2: 4.8, К3: 1, К4: 1.7, К5: 1, К6: 1, К строк: 1, К бонус-малус: 0.89, К зменшувальний: 1

Страховий платіж
100 USD

Способи перевірки чинності:
- http://www.mtsbu.ua/, розділ «Перевірка чинності»
- Телефон МТСБУ: 0-800-608-800
- Електронний Європротокол: https://dtp.mtsbu.ua";
            return policyText;
        }

        static ReplyKeyboardMarkup GetConfirmationKeyboard()
        {
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "Так", "Ні" }
            })
            {
                ResizeKeyboard = true
            };
        }

        static Task HandleErrorAsync(ITelegramBotClient _, Exception ex, CancellationToken __)
        {
            Console.WriteLine($"Помилка в HandleErrorAsync: {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
