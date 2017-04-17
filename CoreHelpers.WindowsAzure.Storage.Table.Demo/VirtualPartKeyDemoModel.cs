﻿using System;
namespace CoreHelpers.WindowsAzure.Storage.Table.Demo
{
	[Storable()]
	[VirtualPartitonKey("{{Value1}}-{{Value2}}")]
	[VirtualRowKey("{{Value2}}-{{Value3}}")]
	public class VirtualPartKeyDemoModel
	{
		public string Value1 { get; set;  }
		public string Value2 { get; set;  }				
		public string Value3 { get; set;  }
	}
}
