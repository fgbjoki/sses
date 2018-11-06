﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace Common.Transaction
{
    /// <summary>
    /// Thrown if user has can't withdraw requested amount.
    /// </summary>
    [Serializable]
    public class NotEnoughResourcesException : Exception
    {
        public NotEnoughResourcesException() : base("Not enough resources on account.")
        {
            //throw new FaultException<NotEnoughResourcesException>(new NotEnoughResourcesException());
        }

        public NotEnoughResourcesException(string message) : base(message)
        {
        }
    }
}
