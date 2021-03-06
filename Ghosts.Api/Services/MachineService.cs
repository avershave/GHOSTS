﻿// Copyright 2017 Carnegie Mellon University. All Rights Reserved. See LICENSE.md file for terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghosts.Api.Code;
using Ghosts.Api.Data;
using Ghosts.Api.Models;
using Ghosts.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Libuv.Internal.Networking;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NLog;
using SimpleTCP;

namespace Ghosts.Api.Services
{
    public interface IMachineService
    {
        Task<List<Machine>> GetAsync(string q, CancellationToken ct);
        Task<Machine> GetByIdAsync(Guid id, CancellationToken ct);
        Task<Machine> FindByValue(Machine machine, CancellationToken ct);
        Task<List<MachineListItem>> GetListAsync(CancellationToken ct);
        Task<Guid> CreateAsync(Machine model, CancellationToken ct);
        Task<Machine> UpdateAsync(Machine model, CancellationToken ct);
        Task<Guid> DeleteAsync(Guid model, CancellationToken ct);
        Task<List<HistoryHealth>> GetMachineHistoryHealth(Guid model, CancellationToken ct);
        Task<List<Machine.MachineHistoryItem>> GetMachineHistory(Guid model, CancellationToken ct);
        Task<TimelineHandler> SendCommand(Guid id, string command, CancellationToken ct);
        Task<List<HistoryTimeline>> GetActivity(Guid id, CancellationToken ct);
    }

    public class MachineService : IMachineService
    {
        private static Logger _log = LogManager.GetCurrentClassLogger();
        private int _lookback = Program.ClientConfig.LookbackRecords;
        private readonly ApplicationDbContext _context;

        public MachineService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<Machine>> GetAsync(string q, CancellationToken ct)
        {
            var list = this._context.Machines.Where(o => o.Status == StatusType.Active).OrderByDescending(o => o.LastReportedUtc).ToList();
            foreach (var item in list)
            {
                item.HistoryHealth = null;
                item.HistoryTimeline = null;
                item.HistoryTrackables = null;
                item.History = null;
            }
            return list;
        }

        public async Task<Machine> FindByValue(Machine machine, CancellationToken ct)
        {
            switch (Program.ClientConfig.MatchMachinesBy.ToLower())
            {
                case "fqdn":
                    return _context.Machines.FirstOrDefault(o => o.FQDN.Contains(machine.FQDN));
                case "host":
                    return _context.Machines.FirstOrDefault(o => o.Host.Contains(machine.Host));
                case "resolvedhost":
                    return _context.Machines.FirstOrDefault(o => o.ResolvedHost.Contains(machine.ResolvedHost));
                default: 
                    return _context.Machines.FirstOrDefault(o => o.Name.Contains(machine.Name));
            }
        }

        public async Task<List<MachineListItem>> GetListAsync(CancellationToken ct)
        {
            var items = new List<MachineListItem>();
            var q = from r in this._context.Machines
                    select new
                    {
                        Id = r.Id,
                        Name = r.Name
                    };
            foreach (var item in q)
                items.Add(new MachineListItem
                {
                    Id = item.Id,
                    Name = item.Name
                });
            return items;
        }

        public async Task<Machine> GetByIdAsync(Guid id, CancellationToken ct)
        {
            var machine = await _context.Machines.FirstOrDefaultAsync(o => o.Id == id, ct);

            if (machine == null)
                return null;

            machine.AddHistoryMachine(await this._context.HistoryMachine.Where(o => o.MachineId == machine.Id)
                .OrderByDescending(o => o.CreatedUtc).Take(_lookback).ToListAsync(ct));
            machine.AddHistoryHealth(await this._context.HistoryHealth.Where(o => o.MachineId == machine.Id)
                .OrderByDescending(o => o.CreatedUtc).Take(_lookback).ToListAsync(ct));
            machine.AddHistoryTimeline(await this._context.HistoryTimeline.Where(o => o.MachineId == machine.Id)
                .OrderByDescending(o => o.CreatedUtc).Take(_lookback).ToListAsync(ct));

            return machine;
        }

        public async Task<Guid> CreateAsync(Machine model, CancellationToken ct)
        {
            model.StatusUp = Machine.UpDownStatus.Up;
            model.LastReportedUtc = DateTime.UtcNow;

            var ex = await _context.Machines.FirstOrDefaultAsync(o => o.Id == model.Id, ct);
            if (ex != null) return ex.Id;

            if (model.Id == Guid.Empty)
                model.Id = Guid.NewGuid();
            _context.Machines.Add(model);

            var operation = await _context.SaveChangesAsync(ct);
            if (operation < 1)
            {
                _log.Error($"Count not create machine: {operation}");
                throw new InvalidOperationException("Could not create Machine");
            }

            AddToGroups(model, ct);

            return model.Id;
        }

        public async Task<Machine> UpdateAsync(Machine model, CancellationToken ct)
        {
            model.StatusUp = Machine.UpDownStatus.Up;
            model.LastReportedUtc = DateTime.UtcNow;

            var ex = await _context.Machines.FirstOrDefaultAsync(o => o.Id == model.Id, ct);
            if (ex == null)
            {
                _log.Error($"Machine not found: {ex}");
                throw new InvalidOperationException("Machine not found");
            }

            _context.Entry(model).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException e)
            {
                _log.Error("MachineService Update save error", e);
            }

            try
            {
                _context.Machines.Update(ex);

                var operation = await _context.SaveChangesAsync(ct);
                if (operation < 1)
                {
                    _log.Error($"Count not update machine: {operation}");
                    throw new InvalidOperationException($"Could not update Machine, operation was {operation}");
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "MachineService Update machine error");
            }

            AddToGroups(model, ct);

            return model;
        }

        public async Task<Guid> DeleteAsync(Guid id, CancellationToken ct)
        {
            var a = await _context.Machines.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (a == null)
            {
                _log.Error($"Machine not found: {id}");
                throw new InvalidOperationException("Machine not found");
            }

            a.Status = StatusType.Deleted;
            _context.Entry(a).State = EntityState.Modified;

            var operation = await _context.SaveChangesAsync(ct);
            if (operation < 1)
            {
                _log.Error($"Could not delete machine: {operation}");
                throw new InvalidOperationException("Could not delete Machine");
            }
            return id;
        }

        public async Task<List<HistoryHealth>> GetMachineHistoryHealth(Guid id, CancellationToken ct)
        {
            return await _context.HistoryHealth
                .Where(o => o.MachineId == id)
                .OrderByDescending(o => o.CreatedUtc)
                .ToListAsync(ct);
        }

        public async Task<List<Machine.MachineHistoryItem>> GetMachineHistory(Guid id, CancellationToken ct)
        {
            return await _context.HistoryMachine.Where(o => o.MachineId == id).ToListAsync(ct);
        }


        public void AddToGroups(Machine model, CancellationToken ct)
        {
            var groups = GroupNames.GetGroupNames(model);

            foreach (var group in groups)
            {
                var existingGroup = this._context.Groups.Include(o => o.GroupMachines).FirstOrDefaultAsync(o => o.Name.Equals(group), ct).Result;

                if (existingGroup != null && existingGroup.Name == group)
                {
                    if (!existingGroup.GroupMachines.Any(o => o.MachineId.Equals(model.Id)))
                    {
                        existingGroup.GroupMachines.Add(new GroupMachine { GroupId = existingGroup.Id, MachineId = model.Id });
                        this._context.SaveChanges();
                    }
                }
                else
                {
                    var newGroup = new Group();
                    newGroup.Name = group;
                    newGroup.Status = StatusType.Active;
                    newGroup.GroupMachines.Add(new GroupMachine { GroupId = newGroup.Id, MachineId = model.Id });
                    this._context.Groups.Add(newGroup);
                    this._context.SaveChanges();
                }
            }
        }

        public async Task<TimelineHandler> SendCommand(Guid id, string command, CancellationToken ct)
        {
            TimelineHandler handler = null;

            var machine = await _context.Machines.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (machine == null)
            {
                _log.Error($"Machine not found: {id}");
                throw new InvalidOperationException("Machine not found");
            }

            try
            {
                var client = new SimpleTcpClient().Connect(machine.HostIp, Program.ClientConfig.ListenerPort);
                client.AutoTrimStrings = true;
                client.Delimiter = 0x13;

                var replyMsg = client.WriteLineAndGetReply(command, TimeSpan.FromSeconds(3));

                var ret = replyMsg.MessageString;
                var index = ret.LastIndexOf("}", StringComparison.InvariantCultureIgnoreCase);
                if (index > 0)
                    ret = ret.Substring(0, index + 1);

                handler = JsonConvert.DeserializeObject<TimelineHandler>(ret);
            }
            catch (Exception e)
            {
                _log.Debug(e);
                throw;
            }

            return handler;
        }

        public async Task<List<HistoryTimeline>> GetActivity(Guid id, CancellationToken ct)
        {
            var machine = await _context.Machines.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (machine == null)
            {
                _log.Error($"Machine not found: {id}");
                throw new InvalidOperationException("Machine not found");
            }

            try
            {
                return this._context.HistoryTimeline.Where(o => o.MachineId == id).OrderByDescending(o=>o.CreatedUtc).Take(50).ToList();
            }
            catch (Exception e)
            {
                _log.Debug(e);
                throw;
            }
        }
    }
}
