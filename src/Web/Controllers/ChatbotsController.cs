using ChatbotApi.Application.Chatbots.Commands.CreateChatbotCommand;
using ChatbotApi.Application.Chatbots.Commands.CreateFlexMessageCommand;
using ChatbotApi.Application.Chatbots.Commands.CreatePreMessageCommand;
using ChatbotApi.Application.Chatbots.Commands.CreatePreMessageFromFileCommand;
using ChatbotApi.Application.Chatbots.Commands.DeleteChatbotCommand;
using ChatbotApi.Application.Chatbots.Commands.DeleteFlexMessageCommand;
using ChatbotApi.Application.Chatbots.Commands.DeleteMemoryFileCommand;
using ChatbotApi.Application.Chatbots.Commands.DeletePreMessageCommand;
using ChatbotApi.Application.Chatbots.Commands.DismissImportErrorCommand;
using ChatbotApi.Application.Chatbots.Commands.EditChatbotCommand;
using ChatbotApi.Application.Chatbots.Commands.EditFlexMessageCommand;
using ChatbotApi.Application.Chatbots.Commands.EditPreMessageCommand;
using ChatbotApi.Application.Chatbots.Commands.ObtainAssistantMessageCommand;
using ChatbotApi.Application.Chatbots.Commands.TogglePluginStateCommand;
using ChatbotApi.Application.Chatbots.Commands.UpdateMemoryFileNameCommand;
using ChatbotApi.Application.Chatbots.Queries.GetChatbotByIdQuery;
using ChatbotApi.Application.Chatbots.Queries.GetChatbotQuery;
using ChatbotApi.Application.Chatbots.Queries.GetErrorsQuery;
using ChatbotApi.Application.Chatbots.Queries.GetFlexMessageByIdQuery;
using ChatbotApi.Application.Chatbots.Queries.GetFlexMessageQuery;
using ChatbotApi.Application.Chatbots.Queries.GetMemoryFileQuery;
using ChatbotApi.Application.Chatbots.Queries.GetModelHarborModelsQuery;
using ChatbotApi.Application.Chatbots.Queries.GetPluginByChatBotQuery;
using ChatbotApi.Application.Chatbots.Queries.GetPreMessageByIdQuery;
using ChatbotApi.Application.Chatbots.Queries.GetPreMessageQuery;
using ChatbotApi.Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatbotApi.Web.Controllers;

[Authorize]
public class ChatbotsController : MvcController
{
    public async Task<IActionResult> Index(GetChatbotQuery query)
    {
        var result = await Mediator.Send(query);
        return View(result);
    }

    public IActionResult Create()
    {
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateChatbotCommand command)
    {
        try
        {
            var result = await Mediator.Send(command);
            return RedirectToAction(nameof(Index), new { createSuccess = true });
        }
        catch (ValidationException e)
        {
            e.AddToModelState(ModelState);
            return View(command);
        }
    }


    public async Task<IActionResult> Edit([FromQuery] GetChatbotByIdQuery query)
    {
        query.ObtainLogo = true;
        var info = await Mediator.Send(query);
        ViewData["Info"] = info;

        // Get model harbor models and create SelectList
        var modelHarborModels = await Mediator.Send(new GetModelHarborModelsQuery()
        {
            SelectedValue = info.ModelName
        });
        ViewData["ModelNameSelectList"] = modelHarborModels;

        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Edit([FromQuery] GetChatbotByIdQuery query, EditChatbotCommand command)
    {
        try
        {
            query.ObtainLogo = true;
            var result = await Mediator.Send(command);
            return RedirectToAction(nameof(Index), new { editSuccess = true });
        }
        catch (ValidationException e)
        {
            e.AddToModelState(ModelState);
            ViewData["Info"] = await Mediator.Send(query);
            return View(command);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Delete(DeleteChatbotCommand command)
    {
        try
        {
            var result = await Mediator.Send(command);
            return Ok();
        }
        catch (ValidationException)
        {
            return Forbid();
        }
    }

    public async Task<IActionResult> Memory(GetPreMessageQuery query, GetChatbotByIdQuery query2,
        GetPluginByChatBotQuery query4, bool onProcess = false)
    {
        var model = await Mediator.Send(query);
        ViewData["Info"] = await Mediator.Send(query2);
        ViewData["Plugins"] = await Mediator.Send(query4);
        ViewData["OnProcess"] = onProcess;
        return View(model);
    }

    public async Task<IActionResult> AddMemory(GetChatbotByIdQuery query, GetPluginByChatBotQuery query3)
    {
        ViewData["Info"] = await Mediator.Send(query);
        ViewData["Plugins"] = await Mediator.Send(query3);
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> AddMemory([FromQuery] GetChatbotByIdQuery query,
        [FromQuery]
        GetPluginByChatBotQuery query3,
        CreatePreMessageCommand command)
    {
        try
        {
            await Mediator.Send(command);
            return RedirectToAction(nameof(Memory), new GetPreMessageQuery { Id = command.Id });
        }
        catch (ValidationException e)
        {
            e.AddToModelState(ModelState);
            ViewData["Info"] = await Mediator.Send(query);
            ViewData["Plugins"] = await Mediator.Send(query3);
            return View(command);
        }
    }

    public async Task<IActionResult> EditMemory(GetPreMessageByIdQuery query, GetPluginByChatBotQuery query3)
    {
        ViewData["Info"] = await Mediator.Send(query);
        ViewData["Plugins"] = await Mediator.Send(query3);
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> EditMemory([FromQuery] GetPreMessageByIdQuery query,
        [FromQuery]
        GetPluginByChatBotQuery query3,
        EditPreMessageCommand command)
    {
        try
        {
            await Mediator.Send(command);
            return RedirectToAction(nameof(Memory), new GetPreMessageQuery { Id = command.Id });
        }
        catch (ValidationException e)
        {
            e.AddToModelState(ModelState);
            ViewData["Info"] = await Mediator.Send(query);
            ViewData["Plugins"] = await Mediator.Send(query3);
            return View(command);
        }
    }

    [HttpPost]
    public async Task<IActionResult> ObtainMessage([FromBody] ObtainAssistantMessageCommand command)
    {
        return Json(await Mediator.Send(command));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteMemory([FromForm] DeletePreMessageCommand command)
    {
        try
        {
            var result = await Mediator.Send(command);
            return result ? Ok() : Forbid();
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
    }


    public async Task<IActionResult> AddMemoryByFile(GetChatbotByIdQuery query)
    {
        ViewData["Info"] = await Mediator.Send(query);
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> AddMemoryByFile([FromQuery] GetChatbotByIdQuery query,
        CreatePreMessageFromFileCommand command)
    {
        try
        {
            await Mediator.Send(command);
            return RedirectToAction(nameof(Memory), new { Id = command.Id, onProcess = true });
        }
        catch (ValidationException e)
        {
            e.AddToModelState(ModelState);
            ViewData["Info"] = await Mediator.Send(query);
            return View(command);
        }
    }

    public async Task<IActionResult> ListMemoryFile(GetChatbotByIdQuery query)
    {
        ViewData["Info"] = await Mediator.Send(query);

        var fileQuery = new GetMemoryFileQuery { Id = query.Id };
        var files = await Mediator.Send(fileQuery);

        return View(files);
    }

    [HttpPost]
    public async Task<IActionResult> EditMemoryFileName([FromQuery] int id, UpdateMemoryFileNameCommand command)
    {
        await Mediator.Send(command);
        return RedirectToAction(nameof(ListMemoryFile), new { Id = id });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteMemoryFile([FromQuery] int id, DeleteMemoryFileCommand command)
    {
        await Mediator.Send(command);
        return RedirectToAction(nameof(ListMemoryFile), new { Id = id });
    }

    public async Task<IActionResult> FlexMessage(int chatbotId)
    {
        var model = await Mediator.Send(new GetFlexMessageQuery() { ChatbotId = chatbotId });
        ViewData["Info"] = await Mediator.Send(new GetChatbotByIdQuery() { Id = chatbotId });
        return View(model);
    }

    public async Task<IActionResult> AddFlexMessage(int chatbotId)
    {
        ViewData["Info"] = await Mediator.Send(new GetChatbotByIdQuery() { Id = chatbotId });
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> AddFlexMessage([FromQuery] int chatbotId, CreateFlexMessageCommand command)
    {
        try
        {
            await Mediator.Send(command);
            return RedirectToAction(nameof(FlexMessage), new GetFlexMessageQuery() { ChatbotId = chatbotId });
        }
        catch (ValidationException e)
        {
            e.AddToModelState(ModelState);
            ViewData["Info"] = await Mediator.Send(new GetChatbotByIdQuery() { Id = chatbotId });
            return View(command);
        }
    }

    public async Task<IActionResult> EditFlexMessage(GetFlexMessageByIdQuery query)
    {
        ViewData["Info"] = await Mediator.Send(query);
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> EditFlexMessage([FromQuery] int id, EditFlexMessageCommand command)
    {
        try
        {
            await Mediator.Send(command);
            return RedirectToAction(nameof(FlexMessage), new GetFlexMessageQuery() { ChatbotId = command.ChatbotId });
        }
        catch (ValidationException e)
        {
            ModelState.Clear();
            e.AddToModelState(ModelState);
            ViewData["Info"] = await Mediator.Send(new GetFlexMessageByIdQuery() { Id = command.Id });
            return View(command);
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteFlexMessage([FromForm] DeleteFlexMessageCommand command)
    {
        try
        {
            var result = await Mediator.Send(command);
            return result ? Ok() : Forbid();
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (ForbiddenAccessException)
        {
            return Forbid();
        }
    }

    public async Task<IActionResult> Plugins(GetChatbotByIdQuery query, GetPluginByChatBotQuery query3)
    {
        ViewData["Info"] = await Mediator.Send(query);
        var plugins = await Mediator.Send(query3);
        return View(plugins);
    }

    [HttpPost]
    public async Task<IActionResult> TogglePluginState([FromBody] TogglePluginStateCommand command)
    {
        var result = await Mediator.Send(command);

        if (result)
        {
            var plugins = await Mediator.Send(new GetPluginByChatBotQuery { Id = command.ChatbotId });
            var updatedPlugin = plugins.FirstOrDefault(p => p.PluginName == command.PluginName);
            if (updatedPlugin != null)
            {
                return PartialView("_PluginRow", updatedPlugin);
            }
        }

        return BadRequest("Failed to update the plugin state.");
    }

    public async Task<IActionResult> Errors(GetErrorsQuery query)
    {
        var model = await Mediator.Send(query);
        ViewData["Id"] = query.Id;
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> DismissError([FromBody] DismissImportErrorCommand command)
    {
        var result = await Mediator.Send(command);
        return result ? Ok() : NotFound();
    }
}
