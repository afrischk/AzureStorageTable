﻿using System;
namespace CoreHelpers.WindowsAzure.Storage.Table
{
	[AttributeUsage(AttributeTargets.Class)]
	public class VirtualRowKeyAttribute : Attribute
	{
		public string RowKeyFormat { get; set; }
		
		public VirtualRowKeyAttribute(string RowKeyFormat) {
			this.RowKeyFormat = RowKeyFormat;
		}
	}
}
