﻿/*
 * Copyright 2025 Rapid Software LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 * 
 * 
 * Product  : Rapid SCADA
 * Module   : ScadaCommon
 * Summary  : Specifies the service control commands
 * 
 * Author   : Mikhail Shiryaev
 * Created  : 2018
 * Modified : 2021
 */

#pragma warning disable 1591 // Missing XML comment for publicly visible type or member

namespace Scada.Agent
{
    /// <summary>
    /// Specifies the service control commands.
    /// <para>Задаёт команды управления сервисом.</para>
    /// </summary>
    public enum ServiceCommand : byte
    {
        Start = 0,
        Stop = 1,
        Restart = 2
    }
}
